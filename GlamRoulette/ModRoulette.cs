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
internal sealed class ModRoulette(Configuration config, PenumbraIpc penumbra)
{
    /// <summary>What each object index is currently set to, so a redraw is only asked for when
    /// something actually changed. A redraw is the expensive part.</summary>
    private readonly Dictionary<int, string> applied = [];

    public int Dressed => applied.Count;

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
        applied.Clear();
    }

    /// <summary>Applies everybody's picks. Returns true if this one had to be redrawn.</summary>
    public bool Apply(int objectIndex, string playerKey)
    {
        if (!config.RandomizeModOptions || config.RandomizedMods.Count == 0 || !penumbra.Available)
            return false;

        var picks = new List<(string Directory, Dictionary<string, IReadOnlyList<string>> Settings)>();
        foreach (var mod in config.RandomizedMods)
        {
            var chosen = Pick(mod, playerKey);
            if (chosen.Count > 0)
                picks.Add((mod.Directory, chosen));
        }

        if (picks.Count == 0)
            return false;

        // Worked out before anything is sent, so a pass that changes nothing costs nothing.
        // Penumbra will not show a settings change without a redraw, and redrawing every pass
        // would leave everyone flickering.
        var signature = string.Join("|", picks.Select(p =>
            p.Directory + "=" + string.Join(",", p.Settings.Select(s => $"{s.Key}:{string.Join("/", s.Value)}"))));

        if (applied.TryGetValue(objectIndex, out var previous) && previous == signature)
            return false;

        var changed = false;
        foreach (var (directory, settings) in picks)
            changed |= penumbra.Apply(objectIndex, directory, settings);

        applied[objectIndex] = signature;

        if (changed)
            penumbra.Redraw(objectIndex);

        return changed;
    }

    /// <summary>
    /// One option per group, derived from who is wearing it rather than drawn - so the same
    /// person keeps the same version of the mod tomorrow, the same way their outfit does.
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> Pick(ModPick mod, string playerKey)
    {
        var picks = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var (group, (options, type)) in GroupsOf(mod.Directory))
        {
            if (options.Length == 0 || mod.SkipGroups.Contains(group))
                continue;

            var seed = Seed(playerKey, mod.Directory, group);

            switch (type)
            {
                case PenumbraIpc.GroupType.Single:
                    picks[group] = [options[(int)(seed % (uint)options.Length)]];
                    break;

                case PenumbraIpc.GroupType.Multi:
                    // Each option is its own coin, so a multi group can come out with any
                    // combination of its parts rather than exactly one of them.
                    var on = new List<string>();
                    for (var i = 0; i < options.Length; i++)
                        if ((seed >> i & 1) == 1)
                            on.Add(options[i]);
                    picks[group] = on;
                    break;

                // Imc and Combining groups are not a list of choices to pick from.
                default:
                    continue;
            }
        }

        return picks;
    }

    /// <summary>Takes our settings off someone and puts them back as they were.</summary>
    public void Release(int objectIndex, bool redraw = true)
    {
        if (!applied.Remove(objectIndex) || !penumbra.Available)
            return;

        penumbra.Release(objectIndex);
        if (redraw)
            penumbra.Redraw(objectIndex);
    }

    public void ReleaseAll()
    {
        foreach (var index in applied.Keys.ToList())
            Release(index);
    }

    public void Sweep(HashSet<int> present)
    {
        // Someone who has walked away is gone along with their temporary settings, so there is
        // nothing to take off - just to stop remembering.
        foreach (var index in applied.Keys.Where(i => !present.Contains(i)).ToList())
            applied.Remove(index);
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
