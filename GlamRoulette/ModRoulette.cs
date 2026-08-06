using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Rolls the option dropdowns on the mods you have picked out - the material, the colour, the
/// bits that are on - so two people wearing the same mod are not wearing the same version of it.
///
/// Only the mods you name, because a size or a body group has to match the wearer and rolling
/// that gives you gaps and clipping rather than variety.
/// </summary>
internal sealed class ModRoulette(Configuration config, PenumbraIpc penumbra, Dyes dyes)
{
    /// <summary>Cached per mod, since reading them is a round trip and they only change when a
    /// mod is reinstalled.</summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, (string[] Options, PenumbraIpc.GroupType Type)>> groups = [];

    public IReadOnlyDictionary<string, (string[] Options, PenumbraIpc.GroupType Type)> GroupsOf(string modDirectory)
    {
        if (groups.TryGetValue(modDirectory, out var cached))
            return cached;

        return groups[modDirectory] = penumbra.Groups(modDirectory);
    }

    public void Forget()
    {
        groups.Clear();
        mine.Clear();
        owners = null;
    }

    /// <summary>Which listed mod each item belongs to, so an outfit can be asked what it is
    /// wearing rather than every mod being pushed at everybody.</summary>
    private Dictionary<ulong, List<string>>? owners;

    private HashSet<string> WornBy(Guid design, uint? shoe)
    {
        owners ??= BuildOwners();

        var worn = new HashSet<string>();
        foreach (var (_, itemId) in dyes.ItemsWorn(design, shoe))
            if (owners.TryGetValue(itemId, out var mods))
                worn.UnionWith(mods);

        return worn;
    }

    private Dictionary<ulong, List<string>> BuildOwners()
    {
        var byName = new Dictionary<string, ulong>();
        foreach (var item in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>())
        {
            var name = item.Name.ExtractText();
            if (name.Length > 0 && item.EquipSlotCategory.RowId != 0)
                byName.TryAdd(name, item.RowId);
        }

        var built = new Dictionary<ulong, List<string>>();
        foreach (var mod in config.RandomizedMods)
        {
            foreach (var name in penumbra.ChangedItems(mod.Directory))
            {
                if (!byName.TryGetValue(name, out var id))
                    continue;

                if (!built.TryGetValue(id, out var mods))
                    built[id] = mods = [];

                mods.Add(mod.Directory);
            }
        }

        Svc.Log.Information($"[GlamRoulette] {config.RandomizedMods.Count} rolled mods cover {built.Count} items");
        return built;
    }

    /// <summary>
    /// What this wearer's mods should be rolled to. Nothing is sent from here - the wardrobe
    /// collects this alongside the clash handling and writes both at once, so one person costs
    /// one redraw rather than two.
    /// </summary>
    public IReadOnlyList<(string Mod, IReadOnlyDictionary<string, IReadOnlyList<string>> Options)> Plan(
        Guid collection, string playerKey, Guid design, uint? shoe)
    {
        if (!config.RandomizeModOptions || config.RandomizedMods.Count == 0 || !penumbra.Available)
            return [];

        // Only the mods this outfit is actually wearing. Rolling the options of all of them for
        // everybody meant every single person needed a redraw to show settings that had no
        // bearing on what they had on.
        var worn = WornBy(design, shoe);
        if (worn.Count == 0)
            return [];

        var picks = new List<(string, IReadOnlyDictionary<string, IReadOnlyList<string>>)>();
        foreach (var mod in config.RandomizedMods.Where(m => worn.Contains(m.Directory)))
        {
            // Yours carries the groups we are not rolling: a temporary setting is built from the
            // mod's own defaults, so a group left unsaid reverts rather than staying put.
            var yours = Yours(collection, mod.Directory);

            // Which makes reading them the thing everything else rests on. If we could not, then
            // rolling the two groups you asked for would quietly put every other group in the mod
            // back to whatever its author chose - your body size among them, and a body size that
            // does not match its wearer is a mesh that stops halfway. Not rolling this one mod is
            // the smaller failure by a long way.
            if (yours.Count == 0 && GroupsOf(mod.Directory).Count > 0)
            {
                Svc.Log.Warning($"[GlamRoulette] Could not read your own settings for " +
                                $"{(mod.Name.Length > 0 ? mod.Name : mod.Directory)}, so it is being " +
                                "left alone rather than reset to the mod's defaults");
                continue;
            }

            var chosen = Pick(mod, playerKey, yours);
            if (chosen.Count > 0)
                picks.Add((mod.Directory, chosen));
        }

        return picks;
    }

    /// <summary>
    /// One option per group, derived from who is wearing it rather than drawn - so the same
    /// person keeps the same version of the mod tomorrow, the same way their outfit does.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> Pick(ModPick mod, string playerKey,
        IReadOnlyDictionary<string, List<string>> yours)
    {
        var picks = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var (group, (options, type)) in GroupsOf(mod.Directory))
        {
            // Groups we are leaving alone still have to be spelled out. A temporary setting is
            // built from the mod's defaults, so saying nothing about a group does not leave it
            // as you set it - it puts it back to whatever the mod author chose.
            if (options.Length == 0 || mod.SkipGroups.Contains(group) || !Rollable(type))
            {
                if (yours.TryGetValue(group, out var mine))
                    picks[group] = mine;

                continue;
            }

            var seed = Seed(playerKey, mod.Directory, group);
            var allowed = mod.Allowed(group);

            if (PicksOne(type))
            {
                // Narrowed to the options that were left ticked, so a mod with forty variants
                // can be held to the handful worth seeing. Unticking every one of them would
                // leave nothing to draw from, which is not a state worth honouring.
                var pool = allowed == null ? options : options.Where(allowed.Contains).ToArray();
                if (pool.Length == 0)
                    pool = options;

                picks[group] = [pool[(int)(seed % (uint)pool.Length)]];
                continue;
            }

            // Tick boxes: each option gets its own coin, so any combination can come up rather
            // than exactly one. An option that was not left ticked is not rolled at all - it
            // keeps whatever the collection has it at, the same rule as an untouched group.
            var mineOn = yours.TryGetValue(group, out var current) ? new HashSet<string>(current) : [];
            var on = new List<string>();

            for (var i = 0; i < options.Length; i++)
            {
                var option = options[i];
                var roll = allowed == null || allowed.Contains(option);

                if (roll ? (seed >> i & 1) == 1 : mineOn.Contains(option))
                    on.Add(option);
            }

            picks[group] = on;
        }

        return picks;
    }

    /// <summary>
    /// Every kind of group Penumbra has is a list of choices, whatever it is called: Single
    /// picks one, and Multi, Combining and Imc all draw as tick boxes and all take the same
    /// list of the names that are on. An Imc option is one attribute of the item being turned
    /// on or off - "show sleeves", "show stockings" - which is the mod's own way of showing and
    /// hiding parts, so rolling one is the point rather than a hazard.
    ///
    /// Anything Penumbra adds later is left alone until it is known to take a list of names.
    /// </summary>
    public static bool Rollable(PenumbraIpc.GroupType type)
        => type is PenumbraIpc.GroupType.Single or PenumbraIpc.GroupType.Multi
            or PenumbraIpc.GroupType.Combining or PenumbraIpc.GroupType.Imc;

    /// <summary>Single groups take exactly one name; every other kind takes the list that is
    /// ticked on.</summary>
    private static bool PicksOne(PenumbraIpc.GroupType type) => type == PenumbraIpc.GroupType.Single;

    /// <summary>
    /// What the collection already says about a mod, cached: this is a round trip per mod per
    /// player and it barely ever changes.
    /// </summary>
    private readonly Dictionary<(Guid, string), (DateTime Read, IReadOnlyDictionary<string, List<string>> Settings)> mine = [];

    private IReadOnlyDictionary<string, List<string>> Yours(Guid collection, string modDirectory)
    {
        if (collection == Guid.Empty)
            return new Dictionary<string, List<string>>();

        if (mine.TryGetValue((collection, modDirectory), out var cached)
            && DateTime.UtcNow - cached.Read < TimeSpan.FromSeconds(30))
            return cached.Settings;

        var settings = penumbra.CurrentSettings(collection, modDirectory);

        // A read that came back with nothing is not worth holding onto for half a minute - it is
        // either a mod with no groups at all, which costs nothing to ask about again, or a failure
        // we would rather retry than keep believing.
        if (settings.Count > 0)
            mine[(collection, modDirectory)] = (DateTime.UtcNow, settings);

        return settings;
    }

    /// <summary>Same hand-rolled hash the dyes use: String.GetHashCode is randomised per
    /// process and would give everyone a new set of options on every restart.</summary>
    private static uint Seed(string playerKey, string modDirectory, string group)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in playerKey)
                hash = (hash ^ c) * 16777619u;
            foreach (var c in modDirectory)
                hash = (hash ^ c) * 16777619u;
            foreach (var c in group)
                hash = (hash ^ c) * 16777619u;

            return hash;
        }
    }
}
