using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ModGuard.Windows;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ModGuard;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/modguard";
    private const int LocalPlayerActorIndex = 0;

    public Configuration Configuration { get; }
    public bool PenumbraAvailable { get; private set; }
    public bool ModsHidden => Configuration.ActiveTempCollection != null;

    private readonly List<string> detectedSyncPlugins = [];
    public IReadOnlyList<string> DetectedSyncPlugins => detectedSyncPlugins;

    public readonly WindowSystem WindowSystem = new("ModGuard");
    private readonly MainWindow mainWindow;

    private readonly ApiVersion penumbraApiVersion;
    private readonly CreateTemporaryCollection createTempCollection;
    private readonly AssignTemporaryCollection assignTempCollection;
    private readonly DeleteTemporaryCollection deleteTempCollection;
    private readonly RedrawObject redrawObject;

    private bool validatedPersistedState;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        penumbraApiVersion = new ApiVersion(PluginInterface);
        createTempCollection = new CreateTemporaryCollection(PluginInterface);
        assignTempCollection = new AssignTemporaryCollection(PluginInterface);
        deleteTempCollection = new DeleteTemporaryCollection(PluginInterface);
        redrawObject = new RedrawObject(PluginInterface);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        Svc.Framework.Update += OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Mod Guard window (hide/restore Penumbra mods around sync plugins)."
        });
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!EzThrottler.Throttle("ModGuardPoll", 2000))
            return;

        PenumbraAvailable = CheckPenumbraAvailable();
        UpdateDetectedSyncPlugins();

        if (!PenumbraAvailable)
            return;

        // If we believe mods are hidden from a previous session, verify the temporary
        // collection actually still exists: a game restart kills temporary collections,
        // leaving a stale guid behind.
        if (!validatedPersistedState)
        {
            validatedPersistedState = true;
            ValidatePersistedHiddenState();
        }

        if (!Configuration.AutoMode || !Player.Available)
            return;

        if (detectedSyncPlugins.Count > 0 && !ModsHidden)
        {
            Svc.Log.Information($"Sync plugin detected ({string.Join(", ", detectedSyncPlugins)}), hiding Penumbra mods");
            HideMods(auto: true);
        }
        else if (detectedSyncPlugins.Count == 0 && ModsHidden && Configuration.WasAutoHidden)
        {
            Svc.Log.Information("No sync plugins loaded anymore, restoring Penumbra mods");
            RestoreMods();
        }
    }

    private bool CheckPenumbraAvailable()
    {
        try
        {
            return penumbraApiVersion.Invoke().Breaking == 5;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateDetectedSyncPlugins()
    {
        detectedSyncPlugins.Clear();
        foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
        {
            if (!plugin.IsLoaded)
                continue;

            if (Configuration.WatchTerms.Any(t =>
                    plugin.InternalName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    plugin.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                if (!detectedSyncPlugins.Contains(plugin.Name))
                    detectedSyncPlugins.Add(plugin.Name);
            }
        }
    }

    private void ValidatePersistedHiddenState()
    {
        if (Configuration.ActiveTempCollection is not { } guid)
            return;

        try
        {
            // Re-asserting the assignment doubles as an existence check: if Penumbra
            // restarted since we hid, the collection is gone and this reports missing.
            var ec = assignTempCollection.Invoke(guid, LocalPlayerActorIndex, forceAssignment: true);
            if (ec is PenumbraApiEc.CollectionMissing)
            {
                Svc.Log.Information("Previously persisted hidden state is stale (Penumbra restarted), clearing it");
                Configuration.ActiveTempCollection = null;
                Configuration.Save();
            }
            else
            {
                Svc.Log.Information("Re-attached to the persisted hidden state from a previous session");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Could not validate the persisted hidden state: {ex.Message}");
        }
    }

    public void HideMods(bool auto)
    {
        if (ModsHidden)
            return;

        if (!PenumbraAvailable)
        {
            Svc.Chat.PrintError("[ModGuard] Penumbra is not available, cannot hide mods.");
            return;
        }

        try
        {
            var ec = createTempCollection.Invoke("ModGuard", "ModGuard Hidden Mods", out var guid);
            if (ec != PenumbraApiEc.Success)
            {
                Svc.Chat.PrintError($"[ModGuard] Failed to create the hiding collection ({ec}).");
                return;
            }

            var assignEc = assignTempCollection.Invoke(guid, LocalPlayerActorIndex, forceAssignment: true);
            if (assignEc != PenumbraApiEc.Success)
            {
                Svc.Chat.PrintError($"[ModGuard] Failed to assign the hiding collection ({assignEc}). Are you logged in?");
                deleteTempCollection.Invoke(guid);
                return;
            }

            Configuration.ActiveTempCollection = guid;
            Configuration.WasAutoHidden = auto;
            Configuration.Save();

            redrawObject.Invoke(LocalPlayerActorIndex);
            Svc.Chat.Print("[ModGuard] Penumbra mods hidden.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to hide mods via Penumbra IPC: {ex.Message}");
            Svc.Chat.PrintError("[ModGuard] Failed to hide mods, see /xllog.");
        }
    }

    public void RestoreMods()
    {
        if (Configuration.ActiveTempCollection is not { } guid)
            return;

        try
        {
            // Success restores the previous assignment; CollectionMissing means Penumbra
            // restarted since we hid, so mods are already back either way.
            var ec = deleteTempCollection.Invoke(guid);
            if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.CollectionMissing)
            {
                Svc.Chat.PrintError($"[ModGuard] Failed to remove the hiding collection ({ec}).");
                return;
            }

            Configuration.ActiveTempCollection = null;
            Configuration.Save();

            redrawObject.Invoke(LocalPlayerActorIndex);
            Svc.Chat.Print("[ModGuard] Penumbra mods restored.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to restore mods via Penumbra IPC: {ex.Message}");
            Svc.Chat.PrintError("[ModGuard] Failed to restore mods, see /xllog.");
        }
    }

    public void Dispose()
    {
        // Deliberately do NOT restore on unload: failing closed keeps mods private.
        // The persisted guid lets a reloaded Mod Guard restore later, and a game
        // restart clears temporary collections anyway.
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        Svc.Commands.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }
}
