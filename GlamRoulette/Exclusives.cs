using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace GlamRoulette;

/// <summary>
/// Lets two outfit mods that cannot coexist be worn by different people at the same time.
///
/// Two mods built on the same base item replace the same model file, and a collection can only
/// have one winner - so the loser's outfit comes out wearing the winner's mesh. Priority does
/// not help; there is one file and two claims on it.
///
/// A temporary setting can be changed between one person being drawn and the next though, and a
/// model keeps what it was built with, so the choice does not have to be made once for everyone.
/// Each player is drawn with the one mod their outfit actually needs switched on and the rest
/// switched off, and the pair can be in the pool together.
/// </summary>
internal sealed class Exclusives(Configuration config, PenumbraIpc penumbra, Dyes dyes)
{
    /// <summary>Which mod each item belongs to, built from what the listed mods change.</summary>
    private Dictionary<ulong, List<string>>? owners;

    /// <summary>
    /// Which mods actually fight each other, worked out rather than grouped by hand: two mods
    /// that change the same item are after the same files, and two that have no item in common
    /// have no quarrel. So every clashing pair you own can go in one list and a mod is only
    /// ever switched off to make room for one it was really in the way of.
    /// </summary>
    private Dictionary<string, HashSet<string>>? rivals;

    /// <summary>How many items each listed mod claims. A mod that exists for one pair of boots
    /// is more specific about them than an outfit that happens to include a pair.</summary>
    private Dictionary<string, int>? sizes;

    /// <summary>How many of the listed mods actually have a rival among the others, so a list
    /// with a typo or a mod that clashes with nothing is visible rather than silent.</summary>
    public int Clashing
    {
        get
        {
            owners ??= Build();
            return rivals!.Count;
        }
    }

    public void Forget()
    {
        owners = null;
        rivals = null;
        sizes = null;
    }

    /// <summary>
    /// Which of the listed mods have to be on and which off for one outfit to come out right.
    /// Nothing is sent from here - the wardrobe collects this together with the rolled options
    /// and writes both at once, so one person costs one redraw rather than two.
    /// </summary>
    public IReadOnlyList<(string Mod, bool Enabled)> Plan(Guid design)
    {
        if (config.ExclusiveMods.Count < 2 || !penumbra.Available)
            return [];

        var wanted = OwnerOf(design);

        // No idea which of them this outfit belongs to - leave every one of them alone rather
        // than guessing and turning off the one that was working.
        if (wanted == null)
            return [];

        // Only the ones this outfit's mod was actually fighting. A second clashing pair in the
        // same list has nothing to do with this player and is left switched on.
        var fighting = rivals!.GetValueOrDefault(wanted) ?? [];

        var plan = new List<(string, bool)>();
        foreach (var mod in config.ExclusiveMods)
        {
            if (mod.Directory != wanted && !fighting.Contains(mod.Directory))
                continue;

            plan.Add((mod.Directory, mod.Directory == wanted));
        }

        return plan;
    }

    /// <summary>
    /// Which of the listed mods an outfit is wearing, decided by the items in it rather than by
    /// anything having to be paired up by hand: a mod says which items it changes, a design says
    /// which items it puts on, and the overlap is the answer.
    ///
    /// Only an item that one mod alone changes actually proves anything. A shared one - the
    /// boots two mods both replace - proves nothing on its own, so those are counted separately
    /// and only consulted when nothing else has spoken.
    /// </summary>
    private string? OwnerOf(Guid design)
    {
        owners ??= Build();

        var proof = new Dictionary<string, int>();
        var contested = new HashSet<string>();

        foreach (var (_, itemId) in dyes.ItemsOf(design))
        {
            if (!owners.TryGetValue(itemId, out var mods))
                continue;

            if (mods.Count == 1)
                proof[mods[0]] = proof.GetValueOrDefault(mods[0]) + 1;
            else
                foreach (var mod in mods)
                    contested.Add(mod);
        }

        // An outfit wearing pieces only one of them supplies is that one's, however many of the
        // shared pieces it also wears.
        if (proof.Count > 0)
            return proof.MaxBy(v => v.Value).Key;

        if (contested.Count == 0)
            return null;

        // Nothing but shared pieces: a design wearing the contested boots and none of either
        // outfit wants the mod that exists for those boots, not the outfit that happens to
        // include a pair. The one claiming fewest items is the specific one.
        return contested.MinBy(m => sizes!.GetValueOrDefault(m, int.MaxValue));
    }

    private Dictionary<ulong, List<string>> Build()
    {
        var byName = new Dictionary<string, ulong>();
        foreach (var item in Svc.Data.GetExcelSheet<Item>())
        {
            var name = item.Name.ExtractText();
            if (name.Length > 0 && item.EquipSlotCategory.RowId != 0)
                byName.TryAdd(name, item.RowId);
        }

        var built = new Dictionary<ulong, List<string>>();
        sizes = [];

        foreach (var mod in config.ExclusiveMods)
        {
            foreach (var name in penumbra.ChangedItems(mod.Directory))
            {
                if (!byName.TryGetValue(name, out var id))
                    continue;

                if (!built.TryGetValue(id, out var mods))
                    built[id] = mods = [];

                mods.Add(mod.Directory);
                sizes[mod.Directory] = sizes.GetValueOrDefault(mod.Directory) + 1;
            }
        }

        // Anything sharing an item is after the same files, so those are the pairs that fight.
        rivals = [];
        foreach (var mods in built.Values.Where(m => m.Count > 1))
        {
            foreach (var mod in mods)
            {
                if (!rivals.TryGetValue(mod, out var against))
                    rivals[mod] = against = [];

                foreach (var other in mods.Where(o => o != mod))
                    against.Add(other);
            }
        }

        Svc.Log.Information($"[GlamRoulette] {config.ExclusiveMods.Count} listed mods cover {built.Count} items, " +
                            $"{rivals.Count} of them with someone to fight");
        return built;
    }

}
