using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Penumbra's published IPC, subscribed by label so this does not carry a copy of its API around.
///
/// The call that matters is SetTemporaryModSettingsPlayer, which is not quite what its name
/// suggests: it takes an object index but writes to whatever collection that object is being drawn
/// with, and everyone else in that collection is written to along with them. Giving two people
/// different options is therefore a matter of timing rather than of addressing - see
/// <see cref="CollectionState"/>.
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
    private readonly ICallGateSubscriber<Guid, int, int> removeAllForCollection;
    private readonly ICallGateSubscriber<int, int, object?> redraw;
    private readonly ICallGateSubscriber<int, (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) Collection)> collectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, bool,
        (int Result, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings, bool Inherited)? Data)> currentSettings;

    private readonly ICallGateSubscriber<object?> initialized;

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
        removeAllForCollection = Svc.PluginInterface
            .GetIpcSubscriber<Guid, int, int>("Penumbra.RemoveAllTemporaryModSettings.V5");
        redraw = Svc.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5");
        collectionForObject = Svc.PluginInterface
            .GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        currentSettings = Svc.PluginInterface
            .GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(
                "Penumbra.GetCurrentModSettings.V5");
        initialized = Svc.PluginInterface.GetIpcSubscriber<object?>("Penumbra.Initialized");
    }

    /// <summary>
    /// Penumbra coming back up. Temporary settings do not survive it, so anything we thought a
    /// collection was holding has to be treated as gone - otherwise everyone stays in the mod's
    /// defaults and we never notice, having convinced ourselves it was all already in place.
    /// </summary>
    public void OnRestart(Action handler) => initialized.Subscribe(handler);

    public void StopWatching(Action handler)
    {
        try
        {
            initialized.Unsubscribe(handler);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not stop watching Penumbra: {ex.Message}");
        }
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
    /// Sets one mod - on or off, and with which options - in the collection this player is being
    /// drawn with. Inherit is false so the mod is definitely at what we say rather than at what it
    /// inherits; the key stays zero so these are not locked to us and can always be cleared by
    /// hand. Only the model built after this call carries the change, which is what makes it
    /// per-person despite the collection being shared.
    /// </summary>
    public bool Apply(int objectIndex, string modDirectory, bool enabled,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        try
        {
            var result = setForPlayer.InvokeFunc(objectIndex, modDirectory, string.Empty,
                (false, enabled, 0, settings), "Glam Roulette", 0);

            if (result == 0)
            {
                refused.Remove(modDirectory);
                return true;
            }

            // A refusal is not a one-off. Nothing was written, so the collection never comes to
            // hold what the wearer wants, and every rebuild asks again and buys another redraw -
            // a mod that Penumbra will not take is a person who is redrawn forever. That was
            // noted at Debug, which Dalamud's log does not carry, so the loop was visible and
            // its cause was not. Once per mod per answer, so it cannot become the log.
            if (refused.Add(modDirectory))
                Svc.Log.Warning($"[GlamRoulette] Penumbra would not set {modDirectory}: {result}. " +
                                $"Asked for {Describe(enabled, settings)}");

            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not set options on {modDirectory}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Mods Penumbra has already turned down, so it is said once rather than every
    /// pass for as long as the wearer stands there.</summary>
    private readonly HashSet<string> refused = [];

    /// <summary>What we asked for, for a refusal to be read against the mod's own groups.</summary>
    private static string Describe(bool enabled, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
        => enabled
            ? string.Join("; ", settings.Select(s => $"{s.Key} = [{string.Join(", ", s.Value)}]"))
            : "off";

    /// <summary>Takes every temporary setting back out of a collection.</summary>
    public void Release(Guid collection)
    {
        try
        {
            removeAllForCollection.InvokeFunc(collection, 0);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not clear collection {collection}: {ex.Message}");
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
