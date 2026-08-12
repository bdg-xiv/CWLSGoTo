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
        companions.Clear();
        links.Clear();
        wornBy.Clear();
    }

    /// <summary>
    /// What an option says it needs installed alongside it, as it is written. Holo's outfits do
    /// this: one of the colours is "Effect - Requires Effect Selector modpack", and the effect
    /// itself lives in a separate mod that this option's files only point at. Picking it without
    /// the other mod switched on gets you nothing at all.
    /// </summary>
    public static string? Requirement(string option)
    {
        var at = option.IndexOf("requires", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        var rest = option[(at + "requires".Length)..].Trim(' ', ':', '-', '(', ')', '[', ']', '.', ',');
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>Resolved requirements, since it is a walk of the whole mod list per option and
    /// the answer only changes when something is installed or renamed.</summary>
    private readonly Dictionary<string, (string Directory, string Name)?> companions = [];

    /// <summary>The installed mod an option needs, if it names one and it is there.</summary>
    public (string Directory, string Name)? CompanionOf(string option)
    {
        if (companions.TryGetValue(option, out var known))
            return known;

        return companions[option] = Resolve(option);
    }

    /// <summary>
    /// Matches what the option asks for against the mods you have, on words rather than on the
    /// whole string - "Requires Effect Selector modpack" has to find "[Holo] Effect, Piercing
    /// Selector V1.5.1op", which contains every word it asked for and a few of its own. Where
    /// several would do, the one carrying the fewest words of its own wins as the closest thing
    /// to what was asked for.
    /// </summary>
    private (string Directory, string Name)? Resolve(string option)
    {
        if (Requirement(option) is not { } asked)
            return null;

        var need = Words(asked).Where(w => w is not ("modpack" or "mod" or "pack" or "the")).ToList();
        if (need.Count == 0)
            return null;

        (string Directory, string Name)? best = null;
        var fewest = int.MaxValue;

        foreach (var (directory, name) in penumbra.Mods())
        {
            var words = Words(name);
            if (!need.All(words.Contains) || words.Count >= fewest)
                continue;

            best = (directory, name);
            fewest = words.Count;
        }

        if (best is { } found)
            Svc.Log.Information($"[GlamRoulette] \"{option}\" wants {found.Name}, so it will be " +
                                "switched on and rolled for whoever draws that option");
        else
            Svc.Log.Information($"[GlamRoulette] \"{option}\" wants \"{asked}\", which is not installed " +
                                "under any name I can match");

        return best;
    }

    /// <summary>The words in a name, lowercased. Punctuation is a separator rather than part of
    /// anything, so "Effect, Piercing" and "Effect Piercing" read the same.</summary>
    private static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>();
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        return words;
    }

    /// <summary>Which listed mod each item belongs to, so an outfit can be asked what it is
    /// wearing rather than every mod being pushed at everybody.</summary>
    private Dictionary<ulong, List<string>>? owners;

    /// <summary>Whether an outfit is built on a particular mod, for the window to offer one to
    /// try it on with. Asked of the design's own shoes, since a rolled pair is a per-person
    /// thing and this is a question about the outfit rather than about anybody wearing it.</summary>
    public bool Wears(Guid design, string modDirectory) => WornBy(design, null).Contains(modDirectory);

    /// <summary>What each outfit wears, remembered - the window asks per mod per frame, and
    /// working it out fresh means walking the design's items every time. Cleared with the
    /// rest when the picks change.</summary>
    private readonly Dictionary<(Guid Design, uint? Shoe), HashSet<string>> wornBy = [];

    private HashSet<string> WornBy(Guid design, uint? shoe)
    {
        if (wornBy.TryGetValue((design, shoe), out var cached))
            return cached;

        owners ??= BuildOwners();

        var worn = new HashSet<string>();
        foreach (var (_, itemId) in dyes.ItemsWorn(design, shoe))
            if (owners.TryGetValue(itemId, out var mods))
                worn.UnionWith(mods);

        return wornBy[(design, shoe)] = worn;
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
    public IReadOnlyList<(string Mod, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Options)> Plan(
        Guid collection, string playerKey, Guid design, uint? shoe, int roll)
    {
        if (!config.RandomizeModOptions || config.RandomizedMods.Count == 0 || !penumbra.Available)
            return [];

        // Only the mods this outfit is actually wearing. Rolling the options of all of them for
        // everybody meant every single person needed a redraw to show settings that had no
        // bearing on what they had on.
        var worn = WornBy(design, shoe);
        if (worn.Count == 0)
            return [];

        var picks = new List<(string Mod, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Options)>();
        var needed = new HashSet<string>();

        foreach (var mod in config.RandomizedMods.Where(m => worn.Contains(m.Directory)))
        {
            // Yours carries the groups we are not rolling: a temporary setting is built from the
            // mod's own defaults, so a group left unsaid reverts rather than staying put. The
            // priority comes across the same way and for the same reason.
            var (priority, yours) = Yours(collection, mod.Directory);

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

            var chosen = Pick(mod, playerKey, yours, roll);
            if (chosen.Count > 0)
                picks.Add((mod.Directory, mod.Priority ?? priority, chosen));

            // An option that only points at files in another mod is nothing on its own. Whoever
            // draws one gets that mod as well, switched on and rolled like any other - so the
            // effect varies person to person rather than everybody wearing the same one - and
            // nobody else has it forced on them for an option they did not draw.
            foreach (var option in chosen.SelectMany(g => g.Value))
                if (CompanionOf(option) is { } companion)
                    needed.Add(companion.Directory);
        }

        foreach (var directory in needed.Where(d => picks.All(p => p.Mod != d)))
        {
            // Its own entry if you have made it one, so its options can be narrowed the way any
            // other mod's can. Without one it is simply rolled whole.
            var pick = config.RandomizedMods.FirstOrDefault(m => m.Directory == directory)
                       ?? new ModPick { Directory = directory };

            var (priority, theirs) = Yours(collection, directory);

            // The same guard as above, but only where it can bite: a group we are not rolling is
            // one that has to be carried over from what the collection says, and if that could
            // not be read it would revert to the mod's defaults instead. With nothing skipped
            // every group is spoken for and there is nothing to lose.
            if (pick.SkipGroups.Count > 0 && theirs.Count == 0 && GroupsOf(directory).Count > 0)
                continue;

            var rolled = Pick(pick, playerKey, theirs, roll);
            if (rolled.Count > 0)
                picks.Add((directory, pick.Priority ?? priority, rolled));
        }

        return picks;
    }

    /// <summary>
    /// The groups of a mod that are rolled as one, and the options they can agree on. Two groups
    /// belong together when one's options are all among the other's, which is what one set of
    /// colours spread over seven pieces of an outfit looks like - the odd piece simply missing a
    /// shade. A group with a single option is left out; it would be a subset of everything.
    ///
    /// What they can agree on is what all of them offer. A shade two of the seven have never
    /// heard of cannot be the answer for all seven, so it stops coming up while they are linked -
    /// which is the price of the outfit matching, and worth saying out loud in the window.
    /// </summary>
    public IReadOnlyList<(string Key, IReadOnlyList<string> Shared, IReadOnlyList<string> Groups)> LinksOf(ModPick mod)
    {
        if (links.TryGetValue(mod.Directory, out var cached))
            return cached;

        // Compared on what can actually come up rather than on everything a group holds. Two
        // groups whose full lists nest but whose ticked options have nothing in common are not
        // one question asked twice, they are two questions - Demon Queen's toggles are exactly
        // that, and matching on the full lists put them together with no answer to give.
        var groups = GroupsOf(mod.Directory)
            .Where(g => Rollable(g.Value.Type) && !mod.SkipGroups.Contains(g.Key) && g.Value.Options.Length > 1)
            .ToDictionary(g => g.Key, g => Pool(mod, g.Key, g.Value.Options));

        // Compared and clustered on flattened names, so an author's inconsistent spelling
        // does not split what is plainly one question. A group whose options flatten into
        // each other cannot be disambiguated that way and stays out of the clustering.
        var names = groups.Keys
            .Where(n => groups[n].Length > 1
                        && groups[n].Select(Canon).Distinct().Count() == groups[n].Length)
            .ToList();
        var parent = names.ToDictionary(n => n, n => n);

        string Find(string name) => parent[name] == name ? name : parent[name] = Find(parent[name]);

        for (var i = 0; i < names.Count; i++)
        for (var j = i + 1; j < names.Count; j++)
        {
            var a = new HashSet<string>(groups[names[i]].Select(Canon));
            var b = new HashSet<string>(groups[names[j]].Select(Canon));
            if (a.IsSubsetOf(b) || b.IsSubsetOf(a))
                parent[Find(names[i])] = Find(names[j]);
        }

        var built = new List<(string, IReadOnlyList<string>, IReadOnlyList<string>)>();

        foreach (var cluster in names.GroupBy(Find).Where(c => c.Count() > 1))
        {
            var members = cluster.OrderBy(n => n, StringComparer.Ordinal).ToList();

            // In the order of the first group's own list, so the same cluster reads the same way
            // twice and a seed means the same thing tomorrow.
            var shared = groups[members[0]]
                .Where(o => members.All(m => groups[m].Any(option => Canon(option) == Canon(o))))
                .ToList();

            // Nothing all of them offer is nothing to answer with, so they are not a cluster
            // after all and each goes back to a roll of its own.
            if (shared.Count > 0)
                built.Add((string.Join(" + ", members), shared, members));
        }

        return links[mod.Directory] = built;
    }

    private readonly Dictionary<string, IReadOnlyList<(string Key, IReadOnlyList<string> Shared, IReadOnlyList<string> Groups)>> links = [];

    /// <summary>Where an option sits in a list, or -1. IReadOnlyList has no IndexOf of its
    /// own and the lists here are a handful of names long.</summary>
    private static int IndexIn(IReadOnlyList<string>? list, string option)
    {
        for (var i = 0; i < (list?.Count ?? 0); i++)
            if (list![i] == option)
                return i;

        return -1;
    }

    /// <summary>An option's name with the spelling differences flattened - case, spaces,
    /// punctuation and a plural s - because the same texture is "Fishnet 0%" in one group,
    /// "Fishnet 0" in the next and "Laces" in a third, and matching on the letter of it
    /// left groups out of clusters they plainly belong to.</summary>
    private static string Canon(string option)
    {
        Span<char> kept = stackalloc char[option.Length];
        var n = 0;
        foreach (var c in option)
            if (char.IsLetterOrDigit(c))
                kept[n++] = char.ToLowerInvariant(c);

        if (n > 1 && kept[n - 1] == 's')
            n--;

        return new string(kept[..n]);
    }

    /// <summary>Where an option sits in a list by flattened name, or -1 - so "Fishnet 0"
    /// finds the coin that "Fishnet 0%" flipped.</summary>
    private static int IndexOfCanon(IReadOnlyList<string>? list, string option)
    {
        var canon = Canon(option);
        for (var i = 0; i < (list?.Count ?? 0); i++)
            if (Canon(list![i]) == canon)
                return i;

        return -1;
    }

    /// <summary>One group's own spelling of a shared answer - the cluster's list carries the
    /// first group's names, and this group may spell the same thing its own way.</summary>
    private static string OwnSpelling(string[] options, string shared)
    {
        var canon = Canon(shared);
        foreach (var option in options)
            if (option == shared || Canon(option) == canon)
                return option;

        return shared;
    }

    /// <summary>What a group may draw from: the options left ticked, or all of them, since
    /// unticking every one is not a state worth honouring.</summary>
    private static string[] Pool(ModPick mod, string group, string[] options)
    {
        var allowed = mod.Allowed(group);
        if (allowed == null)
            return options;

        var pool = options.Where(allowed.Contains).ToArray();
        return pool.Length == 0 ? options : pool;
    }

    /// <summary>
    /// One option per group, derived from who is wearing it rather than drawn - so the same
    /// person keeps the same version of the mod tomorrow, the same way their outfit does.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> Pick(ModPick mod, string playerKey,
        IReadOnlyDictionary<string, List<string>> yours, int roll)
    {
        var picks = new Dictionary<string, IReadOnlyList<string>>();

        // Which groups answer together, by group. A cluster with nothing in common is no cluster:
        // there is no answer that would suit all of it, so each of them goes back to its own roll.
        var linked = new Dictionary<string, (string Key, IReadOnlyList<string> Shared)>();
        if (mod.LinkGroups)
            foreach (var (key, shared, members) in LinksOf(mod))
                if (shared.Count > 0)
                    foreach (var member in members)
                        linked[member] = (key, shared);

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

            var allowed = mod.Allowed(group);
            var link = linked.TryGetValue(group, out var found) ? found : default;

            // Linked groups answer off one roll of the cluster rather than one of their own, so
            // every piece of the outfit lands on the same colour.
            var seed = link.Key != null
                ? Seed(playerKey, mod.Directory, link.Key, roll)
                : Seed(playerKey, mod.Directory, group, roll);

            if (PicksOne(type))
            {
                // Narrowed to the options that were left ticked, so a mod with forty variants
                // can be held to the handful worth seeing. Unticking every one of them would
                // leave nothing to draw from, which is not a state worth honouring.
                var pool = link.Shared ?? Pool(mod, group, options);
                var chosen = pool[(int)(seed % (uint)pool.Count)];

                // The shared list speaks the first group's spelling; this group may spell the
                // same answer its own way, and Penumbra only takes the letter of it.
                picks[group] = [link.Key != null ? OwnSpelling(options, chosen) : chosen];
                continue;
            }

            // Tick boxes: each option gets its own coin, so any combination can come up rather
            // than exactly one. An option that was not left ticked is not rolled at all - it
            // keeps whatever the collection has it at, the same rule as an untouched group.
            var mineOn = yours.TryGetValue(group, out var current) ? new HashSet<string>(current) : [];
            var on = new List<string>();

            // Held separately, because a linked group still has to answer for anything the rest
            // of its cluster has never heard of, and that has to be its own coin.
            var own = link.Key != null ? Seed(playerKey, mod.Directory, group, roll) : seed;

            for (var i = 0; i < options.Length; i++)
            {
                var option = options[i];
                var rolled = allowed == null || allowed.Contains(option);

                // A linked option takes its coin from where it sits in the shared list rather
                // than in this group's, so the same one comes up the same way in all of them -
                // matched by flattened name, since the spellings drift between groups.
                var at = IndexOfCanon(link.Shared, option);
                var bit = at >= 0 ? seed >> at & 1 : own >> i & 1;

                if (rolled ? bit == 1 : mineOn.Contains(option))
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
    private readonly Dictionary<(Guid, string), (DateTime Read, int Priority, IReadOnlyDictionary<string, List<string>> Settings)> mine = [];

    private (int Priority, IReadOnlyDictionary<string, List<string>> Settings) Yours(Guid collection, string modDirectory)
    {
        if (collection == Guid.Empty)
            return (0, new Dictionary<string, List<string>>());

        if (mine.TryGetValue((collection, modDirectory), out var cached)
            && DateTime.UtcNow - cached.Read < TimeSpan.FromSeconds(30))
            return (cached.Priority, cached.Settings);

        var (priority, settings) = penumbra.CurrentSettings(collection, modDirectory);

        // A read that came back with nothing is not worth holding onto for half a minute - it is
        // either a mod with no groups at all, which costs nothing to ask about again, or a failure
        // we would rather retry than keep believing.
        if (settings.Count > 0)
            mine[(collection, modDirectory)] = (DateTime.UtcNow, priority, settings);

        return (priority, settings);
    }

    /// <summary>
    /// Same hand-rolled hash the dyes use: String.GetHashCode is randomised per process and
    /// would give everyone a new set of options on every restart.
    ///
    /// The roll counter is in the sum so that a fresh outfit comes with a fresh version of the
    /// mod it is built on. Without it a re-roll changed the design and the dyes and left the
    /// material and the toggles exactly as they were, which is not what dealing somebody another
    /// outfit is supposed to mean.
    /// </summary>
    private static uint Seed(string playerKey, string modDirectory, string group, int roll)
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
            foreach (var b in BitConverter.GetBytes(roll))
                hash = (hash ^ b) * 16777619u;

            return hash;
        }
    }
}
