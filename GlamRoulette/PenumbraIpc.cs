using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Penumbra's published IPC, subscribed by label so this does not carry a copy of its API around.
///
/// The call that matters is SetTemporaryModSettingsPlayer. Mod settings normally belong to a
/// collection, so giving two people different options would otherwise mean a collection each;
/// this sets them against an object index instead, writes nothing to the collection, and comes
/// off again in one call. It is the same door Mare uses to give synced players their own mods.
/// </summary>
internal sealed class PenumbraIpc
{
    /// <summary>Group kinds, from Penumbra.Api.Enums.GroupType. Imc and Combining groups are
    /// not a list of choices and are left alone.</summary>
    public enum GroupType
    {
        Single,
        Multi,
        Imc,
        Combining,
    }

    private readonly ICallGateSubscriber<(int Breaking, int Feature)> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<string, string>> modList;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> changedItems;
    private readonly ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[] Options, int Type)>?> availableSettings;
    private readonly ICallGateSubscriber<int, string, string,
        (bool Inherit, bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings),
        string, int, int> setForPlayer;
    private readonly ICallGateSubscriber<int, int, int> removeAllForPlayer;
    private readonly ICallGateSubscriber<int, int, object?> redraw;
    private readonly ICallGateSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) Collection)> collectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, bool,
        (int Result, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited)? Data)> currentSettings;

    public PenumbraIpc()
    {
        // Every label but the mod list carries its api version, and asking on the bare name
        // reaches nobody at all rather than failing loudly.
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        modList = Svc.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        changedItems = Svc.PluginInterface
            .GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
        availableSettings = Svc.PluginInterface
            .GetIpcSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>("Penumbra.GetAvailableModSettings.V5");
        setForPlayer = Svc.PluginInterface
            .GetIpcSubscriber<int, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>(
                "Penumbra.SetTemporaryModSettingsPlayer.V5");
        removeAllForPlayer = Svc.PluginInterface
            .GetIpcSubscriber<int, int, int>("Penumbra.RemoveAllTemporaryModSettingsPlayer.V5");
        redraw = Svc.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");
        collectionForObject = Svc.PluginInterface
            .GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        currentSettings = Svc.PluginInterface
            .GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(
                "Penumbra.GetCurrentModSettings.V5");
    }

    /// <summary>The items a mod changes, by name - Penumbra's own Changed Items list.</summary>
    public IReadOnlyCollection<string> ChangedItems(string modDirectory)
    {
        try
        {
            return changedItems.InvokeFunc(modDirectory, string.Empty).Keys;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read what {modDirectory} changes: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Switches a whole mod on or off for one player and nobody else. Two mods that replace the
    /// same model file can only have one winner in a collection, but a temporary setting is per
    /// object, so each person can be given the one their outfit actually needs.
    /// </summary>
    public bool Enable(int objectIndex, string modDirectory, bool enabled)
    {
        try
        {
            var result = setForPlayer.InvokeFunc(objectIndex, modDirectory, string.Empty,
                (false, enabled, 0, new Dictionary<string, IReadOnlyList<string>>()), "Glam Roulette", 0);

            return result == 0;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not switch {modDirectory} for object {objectIndex}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The collection a player is actually being drawn with.</summary>
    public Guid CollectionOf(int objectIndex)
    {
        try
        {
            var (valid, _, collection) = collectionForObject.InvokeFunc(objectIndex);
            return valid ? collection.Id : Guid.Empty;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read the collection of object {objectIndex}: {ex.Message}");
            return Guid.Empty;
        }
    }

    /// <summary>
    /// What a mod is set to in a collection, before any of our temporary settings - Penumbra
    /// leaves those out of this deliberately. Needed because a temporary setting starts from the
    /// mod's own defaults, so a group we say nothing about does not stay as you left it, it
    /// reverts. Handing back what the collection says is how "leave this one alone" is done.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> CurrentSettings(Guid collection, string modDirectory)
    {
        try
        {
            var (result, data) = currentSettings.InvokeFunc(collection, modDirectory, string.Empty, false);
            if (result == 0 && data is { } settings)
                return settings.Settings;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read the settings of {modDirectory}: {ex.Message}");
        }

        return new Dictionary<string, List<string>>();
    }

    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.InvokeFunc().Breaking >= 5;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Every installed mod, by directory and display name.</summary>
    public IReadOnlyDictionary<string, string> Mods()
    {
        try
        {
            return modList.InvokeFunc();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read Penumbra's mod list: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>A mod's option groups and what each one offers.</summary>
    public IReadOnlyDictionary<string, (string[] Options, GroupType Type)> Groups(string modDirectory)
    {
        var groups = new Dictionary<string, (string[], GroupType)>();

        try
        {
            var raw = availableSettings.InvokeFunc(modDirectory, string.Empty);
            if (raw == null)
                return groups;

            foreach (var (name, (options, type)) in raw)
                groups[name] = (options, (GroupType)type);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read the options of {modDirectory}: {ex.Message}");
        }

        return groups;
    }

    /// <summary>
    /// Sets one mod's options for one player and nobody else. Inherit is false and enabled true
    /// so the mod is definitely on for them, whatever the collection says; the key stays zero so
    /// these are not locked to us and can always be cleared by hand.
    /// </summary>
    public bool Apply(int objectIndex, string modDirectory, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        try
        {
            var result = setForPlayer.InvokeFunc(objectIndex, modDirectory, string.Empty,
                (false, true, 0, settings), "Glam Roulette", 0);

            if (result == 0)
                return true;

            Svc.Log.Debug($"[GlamRoulette] Penumbra refused {modDirectory} for object {objectIndex}: {result}");
            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not set options on {modDirectory}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Takes every temporary setting of ours off one player.</summary>
    public void Release(int objectIndex)
    {
        try
        {
            removeAllForPlayer.InvokeFunc(objectIndex, 0);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not clear object {objectIndex}: {ex.Message}");
        }
    }

    /// <summary>Mod settings only take hold on a redraw, unlike a glamour.</summary>
    public void Redraw(int objectIndex)
    {
        try
        {
            redraw.InvokeAction(objectIndex, 0);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not redraw object {objectIndex}: {ex.Message}");
        }
    }
}
