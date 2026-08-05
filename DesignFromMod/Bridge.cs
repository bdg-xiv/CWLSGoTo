using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;

namespace DesignFromMod;

/// <summary>
/// Turns the list of items a Penumbra mod changes into a Glamourer design wearing them.
///
/// Penumbra says what a mod touches by item name; the sheet turns each name into an id and the
/// slot it belongs in; Glamourer takes a design as json. The only awkward part is that a mod
/// usually covers several slots and occasionally two items for one, in which case the first is
/// used and the rest are named in the report rather than quietly dropped.
/// </summary>
internal sealed class Bridge
{
    private readonly ICallGateSubscriber<(int Breaking, int Feature)> penumbraVersion;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> changedItems;
    private readonly ICallGateSubscriber<Dictionary<string, string>> modList;
    private readonly ICallGateSubscriber<(int Major, int Minor)> glamourerVersion;
    private readonly ICallGateSubscriber<string, string, (int Result, Guid Id)> addDesign;

    private IReadOnlyDictionary<string, (uint Id, string Slot)>? items;

    public Bridge()
    {
        penumbraVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        changedItems = Svc.PluginInterface
            .GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
        modList = Svc.PluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        glamourerVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        addDesign = Svc.PluginInterface.GetIpcSubscriber<string, string, (int, Guid)>("Glamourer.AddDesign");
    }

    public bool PenumbraReady => Try(() => penumbraVersion.InvokeFunc().Breaking >= 5);
    public bool GlamourerReady => Try(() => glamourerVersion.InvokeFunc().Major >= 1);

    public string NameOf(string modDirectory)
        => Try(() => modList.InvokeFunc().TryGetValue(modDirectory, out var name) ? name : modDirectory, modDirectory);

    /// <summary>What a mod would put on, without making anything of it yet.</summary>
    public List<(string Item, uint Id, string Slot)> Wearable(string modDirectory)
    {
        var wearable = new List<(string, uint, string)>();
        items ??= Slots.ByName();

        try
        {
            foreach (var name in changedItems.InvokeFunc(modDirectory, string.Empty).Keys)
                if (items.TryGetValue(name, out var item))
                    wearable.Add((name, item.Id, item.Slot));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[DesignFromMod] Could not read what {modDirectory} changes: {ex.Message}");
        }

        // In the order a design lists them, so the report reads like the equipment panel.
        return wearable.OrderBy(w => Array.IndexOf(Slots.Order, w.Item3)).ToList();
    }

    /// <summary>
    /// Builds the design and hands it to Glamourer. Only the slots the mod actually covers are
    /// marked to apply - a design that also blanked every other slot would strip the wearer
    /// rather than dress them.
    /// </summary>
    public (bool Made, string Report) Create(string modDirectory, string designName)
    {
        var wearable = Wearable(modDirectory);
        if (wearable.Count == 0)
            return (false, "Nothing in this mod is an item that can be worn.");

        var equipment = new JObject();
        var used = new HashSet<string>();
        var taken = new List<string>();
        var skipped = new List<string>();

        foreach (var (name, id, slot) in wearable)
        {
            if (!used.Add(slot))
            {
                skipped.Add($"{name} ({slot})");
                continue;
            }

            equipment[slot] = new JObject
            {
                ["ItemId"] = id,
                ["Apply"] = true,
                ["ApplyStain"] = false,
                ["ApplyCrest"] = false,
                ["Crest"] = false,
            };

            taken.Add($"{name} -> {slot}");
        }

        // An empty customization block rather than none at all: Glamourer complains at the user
        // about a design with no customization data, and there is nothing to complain about -
        // this design is meant to be equipment only.
        var design = new JObject
        {
            ["FileVersion"] = 1,
            ["Customize"] = new JObject(),
            ["Equipment"] = equipment,
        };

        try
        {
            var (result, _) = addDesign.InvokeFunc(design.ToString(), designName);
            if (result != 0)
                return (false, $"Glamourer would not take the design ({result}).");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[DesignFromMod] Glamourer refused the design: {ex.Message}");
            return (false, $"Glamourer refused the design: {ex.Message}");
        }

        var report = $"Made \"{designName}\" with {taken.Count} slot(s): {string.Join(", ", taken)}.";
        if (skipped.Count > 0)
            report += $" Left out, slot already taken: {string.Join(", ", skipped)}.";

        return (true, report);
    }

    private static T Try<T>(Func<T> call, T fallback = default!)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }
}
