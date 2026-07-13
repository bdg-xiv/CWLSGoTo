using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using ModGuard.Windows;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.Linq;
using GlamourerApiVersion = Glamourer.Api.IpcSubscribers.ApiVersion;
using PenumbraApiVersion = Penumbra.Api.IpcSubscribers.ApiVersion;

namespace ModGuard;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/modguard";
    private const int LocalPlayerActorIndex = 0;
    // "MODG" - lock key used when reverting Glamourer state so automation or other
    // plugins can't reapply designs while hidden.
    private const uint GlamourerLockKey = 0x4D4F4447;

    public Configuration Configuration { get; }
    public bool PenumbraAvailable { get; private set; }
    public bool GlamourerAvailable { get; private set; }
    public bool ModsHidden => Configuration.ActiveTempCollection != null || Configuration.SavedGlamourerState != null;

    private readonly List<(string Name, string InternalName)> detectedSyncPlugins = [];
    public IReadOnlyList<(string Name, string InternalName)> DetectedSyncPlugins => detectedSyncPlugins;

    // Set while waiting for unloaded sync plugins to actually disappear before restoring.
    public bool PendingRestore { get; private set; }
    private DateTime pendingRestoreDeadline = DateTime.MinValue;

    public readonly WindowSystem WindowSystem = new("ModGuard");
    private readonly MainWindow mainWindow;

    private readonly PenumbraApiVersion penumbraApiVersion;
    private readonly CreateTemporaryCollection createTempCollection;
    private readonly AssignTemporaryCollection assignTempCollection;
    private readonly DeleteTemporaryCollection deleteTempCollection;
    private readonly RedrawObject redrawObject;

    private readonly GlamourerApiVersion glamourerApiVersion;
    private readonly GetStateBase64 glamourerGetState;
    private readonly ApplyState glamourerApplyState;
    private readonly RevertState glamourerRevertState;
    private readonly UnlockState glamourerUnlockState;

    private bool validatedPersistedState;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        penumbraApiVersion = new PenumbraApiVersion(PluginInterface);
        createTempCollection = new CreateTemporaryCollection(PluginInterface);
        assignTempCollection = new AssignTemporaryCollection(PluginInterface);
        deleteTempCollection = new DeleteTemporaryCollection(PluginInterface);
        redrawObject = new RedrawObject(PluginInterface);

        glamourerApiVersion = new GlamourerApiVersion(PluginInterface);
        glamourerGetState = new GetStateBase64(PluginInterface);
        glamourerApplyState = new ApplyState(PluginInterface);
        glamourerRevertState = new RevertState(PluginInterface);
        glamourerUnlockState = new UnlockState(PluginInterface);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        Svc.Framework.Update += OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Mod Guard window (hide/restore Penumbra mods and Glamourer state around sync plugins)."
        });
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!EzThrottler.Throttle("ModGuardPoll", 2000))
            return;

        PenumbraAvailable = CheckAvailable(() => penumbraApiVersion.Invoke().Breaking == 5);
        GlamourerAvailable = CheckAvailable(() =>
        {
            glamourerApiVersion.Invoke();
            return true;
        });
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

        // A restore is queued behind sync plugins unloading; fire it once they're gone.
        if (PendingRestore)
        {
            if (detectedSyncPlugins.Count == 0)
            {
                PendingRestore = false;
                DoRestore();
            }
            else if (DateTime.UtcNow > pendingRestoreDeadline)
            {
                PendingRestore = false;
                Svc.Chat.PrintError("[ModGuard] Sync plugins are still loaded, keeping mods hidden.");
            }
            return;
        }

        if (!Configuration.AutoMode || !Player.Available)
            return;

        if (detectedSyncPlugins.Count > 0 && !ModsHidden)
        {
            Svc.Log.Information($"Sync plugin detected ({string.Join(", ", detectedSyncPlugins.Select(p => p.Name))}), hiding mods");
            HideMods(auto: true);
        }
        else if (detectedSyncPlugins.Count == 0 && ModsHidden && Configuration.WasAutoHidden)
        {
            Svc.Log.Information("No sync plugins loaded anymore, restoring mods");
            RestoreMods();
        }
    }

    private static bool CheckAvailable(Func<bool> check)
    {
        try
        {
            return check();
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
                if (!detectedSyncPlugins.Any(p => p.InternalName == plugin.InternalName))
                    detectedSyncPlugins.Add((plugin.Name, plugin.InternalName));
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
                Svc.Log.Information("Previously persisted hidden state is stale (game restarted), clearing it");
                Configuration.ActiveTempCollection = null;
                // Logging back in re-applied Glamourer automation already; reapplying a
                // stale captured state later could overwrite newer changes.
                Configuration.SavedGlamourerState = null;
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

        var hiddenParts = new List<string>();

        if (HidePenumbra())
            hiddenParts.Add("Penumbra mods");

        if (Configuration.IncludeGlamourer && HideGlamourer())
            hiddenParts.Add("Glamourer state");

        if (hiddenParts.Count == 0)
        {
            Svc.Chat.PrintError("[ModGuard] Nothing could be hidden - is Penumbra loaded and are you logged in?");
            return;
        }

        Configuration.WasAutoHidden = auto;
        Configuration.Save();

        TryRedrawSelf();
        Svc.Chat.Print($"[ModGuard] Hidden: {string.Join(" + ", hiddenParts)}.");
    }

    private bool HidePenumbra()
    {
        if (!PenumbraAvailable)
        {
            Svc.Log.Warning("Penumbra is not available, cannot hide mods");
            return false;
        }

        try
        {
            var ec = createTempCollection.Invoke("ModGuard", "ModGuard Hidden Mods", out var guid);
            if (ec != PenumbraApiEc.Success)
            {
                Svc.Log.Warning($"Failed to create the hiding collection ({ec})");
                return false;
            }

            var assignEc = assignTempCollection.Invoke(guid, LocalPlayerActorIndex, forceAssignment: true);
            if (assignEc != PenumbraApiEc.Success)
            {
                Svc.Log.Warning($"Failed to assign the hiding collection ({assignEc})");
                deleteTempCollection.Invoke(guid);
                return false;
            }

            Configuration.ActiveTempCollection = guid;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to hide mods via Penumbra IPC: {ex.Message}");
            return false;
        }
    }

    private bool HideGlamourer()
    {
        if (!GlamourerAvailable)
        {
            Svc.Log.Warning("Glamourer is not available, skipping its state");
            return false;
        }

        try
        {
            var (getEc, state) = glamourerGetState.Invoke(LocalPlayerActorIndex);
            if (getEc != GlamourerApiEc.Success || state == null)
            {
                Svc.Log.Warning($"Could not capture the current Glamourer state ({getEc}), skipping it");
                return false;
            }

            // Revert to plain game state and lock it with our key so automation or
            // other plugins can't reapply designs while hidden.
            var revertEc = glamourerRevertState.Invoke(LocalPlayerActorIndex, GlamourerLockKey,
                ApplyFlag.Equipment | ApplyFlag.Customization | ApplyFlag.Lock);
            if (revertEc is not GlamourerApiEc.Success and not GlamourerApiEc.NothingDone)
            {
                Svc.Log.Warning($"Could not revert the Glamourer state ({revertEc})");
                return false;
            }

            Configuration.SavedGlamourerState = state;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to hide Glamourer state via IPC: {ex.Message}");
            return false;
        }
    }

    public void RestoreMods()
    {
        if (!ModsHidden || PendingRestore)
            return;

        // Restoring while a sync plugin is still running would share the mods the
        // moment they come back (and auto mode would immediately re-hide them), so
        // unload the sync plugins first and restore once they are actually gone.
        if (detectedSyncPlugins.Count > 0)
        {
            var names = string.Join(", ", detectedSyncPlugins.Select(p => p.Name));
            Svc.Chat.Print($"[ModGuard] Disabling {names} before restoring...");
            foreach (var (_, internalName) in detectedSyncPlugins.ToList())
                TryUnloadPlugin(internalName);

            PendingRestore = true;
            pendingRestoreDeadline = DateTime.UtcNow.AddSeconds(20);
            return;
        }

        DoRestore();
    }

    private void TryUnloadPlugin(string internalName)
    {
        try
        {
            var pluginManager = ECommons.Reflection.DalamudReflector.GetPluginManager();
            var installed = (System.Collections.IEnumerable)pluginManager.GetType()
                .GetProperty("InstalledPlugins")!.GetValue(pluginManager)!;

            foreach (var localPlugin in installed)
            {
                var type = localPlugin.GetType();
                if ((string?)type.GetProperty("InternalName")?.GetValue(localPlugin) != internalName)
                    continue;
                if (type.GetProperty("IsLoaded")?.GetValue(localPlugin) is not true)
                    continue;

                var unload = type.GetMethods().First(m => m.Name == "UnloadAsync");
                var args = unload.GetParameters().Select(p =>
                {
                    var value = p.HasDefaultValue ? p.DefaultValue : null;
                    if (value != null && p.ParameterType.IsEnum && value.GetType() != p.ParameterType)
                        value = Enum.ToObject(p.ParameterType, value);
                    return value;
                }).ToArray();

                Svc.Log.Information($"Unloading sync plugin {internalName}");
                unload.Invoke(localPlugin, args);
                return;
            }

            Svc.Log.Warning($"Could not find a loaded plugin named {internalName} to unload");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to unload {internalName}: {ex.Message}");
        }
    }

    private void DoRestore()
    {
        var restoredParts = new List<string>();
        var failed = false;

        if (Configuration.ActiveTempCollection != null)
        {
            if (RestorePenumbra())
                restoredParts.Add("Penumbra mods");
            else
                failed = true;
        }

        if (Configuration.SavedGlamourerState != null)
        {
            if (RestoreGlamourer())
                restoredParts.Add("Glamourer state");
            else
                failed = true;
        }

        Configuration.Save();

        if (restoredParts.Count > 0)
        {
            TryRedrawSelf();
            Svc.Chat.Print($"[ModGuard] Restored: {string.Join(" + ", restoredParts)}.");
        }

        if (failed)
            Svc.Chat.PrintError("[ModGuard] Some parts could not be restored, see /xllog.");
    }

    private bool RestorePenumbra()
    {
        if (Configuration.ActiveTempCollection is not { } guid)
            return false;

        try
        {
            // Success restores the previous assignment; CollectionMissing means Penumbra
            // restarted since we hid, so mods are already back either way.
            var ec = deleteTempCollection.Invoke(guid);
            if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.CollectionMissing)
            {
                Svc.Log.Warning($"Failed to remove the hiding collection ({ec})");
                return false;
            }

            Configuration.ActiveTempCollection = null;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to restore mods via Penumbra IPC: {ex.Message}");
            return false;
        }
    }

    private bool RestoreGlamourer()
    {
        if (Configuration.SavedGlamourerState is not { } state)
            return false;

        try
        {
            var applyEc = glamourerApplyState.Invoke(state, LocalPlayerActorIndex, GlamourerLockKey,
                ApplyFlag.Equipment | ApplyFlag.Customization);
            if (applyEc is not GlamourerApiEc.Success and not GlamourerApiEc.NothingDone)
            {
                Svc.Log.Warning($"Could not reapply the saved Glamourer state ({applyEc})");
                return false;
            }

            glamourerUnlockState.Invoke(LocalPlayerActorIndex, GlamourerLockKey);
            Configuration.SavedGlamourerState = null;
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to restore Glamourer state via IPC: {ex.Message}");
            return false;
        }
    }

    private void TryRedrawSelf()
    {
        try
        {
            if (PenumbraAvailable)
                redrawObject.Invoke(LocalPlayerActorIndex);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to redraw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Deliberately do NOT restore on unload: failing closed keeps mods private.
        // The persisted state lets a reloaded Mod Guard restore later, and a game
        // restart clears temporary collections and Glamourer locks anyway.
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
