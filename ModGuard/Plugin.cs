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
    private const string ToggleCommandName = "/mg";
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
    private DateTime? pendingRedrawAt;
    private int redrawsRemaining;
    // Set by a manual restore so auto mode doesn't immediately re-hide while a sync
    // plugin is still loaded; cleared by a manual hide or on the next login.
    private bool manuallyRevealed;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Heal configs saved before the deserialization fix that duplicated the list.
        var deduped = Configuration.WatchTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (deduped.Count != Configuration.WatchTerms.Count)
        {
            Configuration.WatchTerms = deduped;
            Configuration.Save();
        }

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
        Svc.ClientState.Login += OnLogin;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Mod Guard window (hide/restore Penumbra mods and Glamourer state around sync plugins)."
        });

        Svc.Commands.AddHandler(ToggleCommandName, new CommandInfo(OnToggleCommand)
        {
            HelpMessage = "Toggles Mod Guard: hides your mods if visible, restores them if hidden."
        });
    }

    // Fresh login re-engages auto-hide (a previous session's manual reveal shouldn't
    // carry over and leave mods exposed to a sync plugin).
    private void OnLogin() => manuallyRevealed = false;

    private void OnCommand(string command, string args) => ToggleMainWindow();

    // Behaves exactly like pressing the currently shown button in the window.
    private void OnToggleCommand(string command, string args)
    {
        // "/mg redraw" forces a clean redraw - handy if the character is stuck black.
        if (args.Trim().Equals("redraw", StringComparison.OrdinalIgnoreCase))
        {
            RequestRedraw();
            Svc.Chat.Print("[ModGuard] Redrawing...");
            return;
        }

        if (PendingRestore)
        {
            Svc.Chat.Print("[ModGuard] Still waiting for sync plugins to unload...");
            return;
        }

        if (ModsHidden)
            RestoreMods();
        else
            HideMods(auto: false);
    }

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Deferred redraws run on their own timing, not behind the 2s poll throttle.
        if (pendingRedrawAt is { } redrawAt)
        {
            if (DateTime.UtcNow >= redrawAt && Player.Available && Player.Interactable)
            {
                DoRedraw();
                // A single redraw can land while Penumbra is still recomputing the
                // collection (black skin); follow up with another after a longer wait
                // so a fully-settled redraw always applies last.
                if (--redrawsRemaining > 0)
                    pendingRedrawAt = DateTime.UtcNow.AddMilliseconds(1500);
                else
                    pendingRedrawAt = null;
            }
        }

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

        // Reset any persisted hidden state on startup: temporary collections are
        // destroyed when the game closes, so a saved guid from a previous session is
        // dead. Re-assigning it left the character on a nonexistent collection (black
        // skin that no redraw could fix). Instead clean up any orphan and start
        // visible; auto mode re-hides freshly on a valid session-local collection.
        if (!validatedPersistedState)
        {
            validatedPersistedState = true;
            ResetPersistedHiddenStateOnStartup();
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

        if (detectedSyncPlugins.Count > 0 && !ModsHidden && !manuallyRevealed)
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

    private void ResetPersistedHiddenStateOnStartup()
    {
        var hadState = Configuration.ActiveTempCollection != null || Configuration.SavedGlamourerState != null;
        if (!hadState)
            return;

        try
        {
            // Delete any orphaned collection: if it somehow survived (plugin reload with
            // Penumbra still running) this removes it and unassigns it; if it's dead
            // (game restart) this is a harmless no-op reporting CollectionMissing.
            if (Configuration.ActiveTempCollection is { } guid)
                deleteTempCollection.Invoke(guid);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Could not clean up the persisted collection on startup: {ex.Message}");
        }

        // Drop any persisted Glamourer lock the previous session may have left dangling.
        try
        {
            glamourerUnlockState.Invoke(LocalPlayerActorIndex, GlamourerLockKey);
        }
        catch
        {
            // Glamourer may be absent; ignore.
        }

        Configuration.ActiveTempCollection = null;
        Configuration.SavedGlamourerState = null;
        Configuration.Save();

        Svc.Log.Information("Reset persisted hidden state on startup; starting with mods visible");
        RequestRedraw();
    }

    public void HideMods(bool auto)
    {
        if (ModsHidden)
            return;

        // A manual hide (or an auto-hide once reveal is no longer in effect) ends any
        // manual-reveal suppression.
        if (!auto)
            manuallyRevealed = false;

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

        RequestRedraw();
        Svc.Chat.Print($"[ModGuard] Hidden: {string.Join(" + ", hiddenParts)}.");

        // With the mods safely hidden, bring back any sync plugins the restore
        // action disabled earlier.
        if (Configuration.UnloadedSyncPlugins.Count > 0)
        {
            Svc.Chat.Print($"[ModGuard] Re-enabling: {string.Join(", ", Configuration.UnloadedSyncPlugins)}.");
            foreach (var internalName in Configuration.UnloadedSyncPlugins.ToList())
                TryLoadPlugin(internalName);

            Configuration.UnloadedSyncPlugins.Clear();
            Configuration.Save();
        }
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

            Svc.Log.Information($"Hid Penumbra mods: created+assigned empty collection {guid} (create={ec}, assign={assignEc})");
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

        // Suppress auto-hide until the next hide/login so a restore isn't instantly
        // undone while a sync plugin is still loaded. Harmless when no sync is present.
        manuallyRevealed = true;

        // Optionally unload the sync plugins first so they don't re-share the mods the
        // moment they come back (and auto mode doesn't immediately re-hide them).
        // Default off: Mare-style plugins render the character continuously and don't
        // tolerate being unloaded mid-draw - killing one leaves the character black.
        if (Configuration.UnloadSyncOnRestore && detectedSyncPlugins.Count > 0)
        {
            var names = string.Join(", ", detectedSyncPlugins.Select(p => p.Name));
            Svc.Chat.Print($"[ModGuard] Disabling {names} before restoring...");
            foreach (var (_, internalName) in detectedSyncPlugins.ToList())
            {
                // Remember what we unloaded so the next hide can re-enable it.
                if (TryUnloadPlugin(internalName) && !Configuration.UnloadedSyncPlugins.Contains(internalName))
                    Configuration.UnloadedSyncPlugins.Add(internalName);
            }
            Configuration.Save();

            PendingRestore = true;
            pendingRestoreDeadline = DateTime.UtcNow.AddSeconds(20);
            return;
        }

        DoRestore();
    }

    private bool TryUnloadPlugin(string internalName)
    {
        try
        {
            var localPlugin = FindLocalPlugin(internalName, mustBeLoaded: true);
            if (localPlugin == null)
            {
                Svc.Log.Warning($"Could not find a loaded plugin named {internalName} to unload");
                return false;
            }

            Svc.Log.Information($"Unloading sync plugin {internalName}");
            InvokeWithDefaults(localPlugin, "UnloadAsync");
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to unload {internalName}: {ex.Message}");
            return false;
        }
    }

    private void TryLoadPlugin(string internalName)
    {
        try
        {
            var localPlugin = FindLocalPlugin(internalName, mustBeLoaded: false);
            if (localPlugin == null)
            {
                Svc.Log.Warning($"Could not find a plugin named {internalName} to re-enable");
                return;
            }

            if (localPlugin.GetType().GetProperty("IsLoaded")?.GetValue(localPlugin) is true)
            {
                Svc.Log.Information($"{internalName} is already loaded again");
                return;
            }

            Svc.Log.Information($"Re-enabling sync plugin {internalName}");
            InvokeWithDefaults(localPlugin, "LoadAsync");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to re-enable {internalName}: {ex.Message}");
        }
    }

    private static object? FindLocalPlugin(string internalName, bool mustBeLoaded)
    {
        var pluginManager = ECommons.Reflection.DalamudReflector.GetPluginManager();
        var installed = (System.Collections.IEnumerable)pluginManager.GetType()
            .GetProperty("InstalledPlugins")!.GetValue(pluginManager)!;

        foreach (var localPlugin in installed)
        {
            var type = localPlugin.GetType();
            if ((string?)type.GetProperty("InternalName")?.GetValue(localPlugin) != internalName)
                continue;
            if (mustBeLoaded && type.GetProperty("IsLoaded")?.GetValue(localPlugin) is not true)
                continue;

            return localPlugin;
        }

        return null;
    }

    private static void InvokeWithDefaults(object localPlugin, string methodName)
    {
        var method = localPlugin.GetType().GetMethods().First(m => m.Name == methodName);
        var args = method.GetParameters().Select(p =>
        {
            // LoadAsync's required reason parameter; everything else has defaults.
            if (p.ParameterType == typeof(PluginLoadReason))
                return (object?)PluginLoadReason.Installer;

            var value = p.HasDefaultValue ? p.DefaultValue : null;
            if (value != null && p.ParameterType.IsEnum && value.GetType() != p.ParameterType)
                value = Enum.ToObject(p.ParameterType, value);
            return value;
        }).ToArray();

        method.Invoke(localPlugin, args);
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
            RequestRedraw();
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

            Svc.Log.Information($"Restored Penumbra mods: deleted collection {guid} (delete={ec})");
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

    // Defer the redraw instead of firing it synchronously: deleting a temporary
    // collection (and applying Glamourer state, which redraws on its own) needs a moment
    // to settle, and redrawing on top of a half-swapped collection renders the skin
    // black. Wait ~1s, then redraw twice (spaced apart) so a fully-settled redraw always
    // applies last. This is worst right after login when the game isn't fully ready.
    private void RequestRedraw()
    {
        redrawsRemaining = 2;
        pendingRedrawAt = DateTime.UtcNow.AddMilliseconds(1000);
    }

    private void DoRedraw()
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
        Svc.ClientState.Login -= OnLogin;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        Svc.Commands.RemoveHandler(CommandName);
        Svc.Commands.RemoveHandler(ToggleCommandName);

        ECommonsMain.Dispose();
    }
}
