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
/// Penumbra's temporary settings are per object though, so the choice does not have to be made
/// once for everyone. Each player gets the one mod their outfit actually needs switched on and
/// the rest switched off, and the pair can be in the pool together.
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

    /// <summary>What each object index was last set to, so nobody is redrawn for no reason.</summary>
    private readonly Dictionary<int, string> applied = [];

    public int Count => applied.Count;

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
        applied.Clear();
    }

    /// <summary>
    /// Switches the listed mods on or off for one player to suit the outfit they have been
    /// given. Returns true when something changed, since that costs a redraw and the outfit has
    /// to go on after it rather than before.
    /// </summary>
    public bool Apply(int objectIndex, Guid design)
    {
        if (config.ExclusiveMods.Count < 2 || !penumbra.Available)
            return false;

        var wanted = OwnerOf(design);

        // No idea which of them this outfit belongs to - leave every one of them alone rather
        // than guessing and turning off the one that was working.
        if (wanted == null)
            return false;

        if (applied.TryGetValue(objectIndex, out var previous) && previous == wanted)
            return false;

        // Only the ones this outfit's mod was actually fighting. A second clashing pair in the
        // same list has nothing to do with this player and is left switched on.
        var fighting = rivals!.GetValueOrDefault(wanted) ?? [];

        var changed = false;
        foreach (var mod in config.ExclusiveMods)
        {
            if (mod.Directory != wanted && !fighting.Contains(mod.Directory))
                continue;

            changed |= penumbra.Enable(objectIndex, mod.Directory, mod.Directory == wanted);
        }

        applied[objectIndex] = wanted;

        if (changed)
        {
            penumbra.Redraw(objectIndex);
            Svc.Log.Debug($"[GlamRoulette] Object {objectIndex} is on {wanted} alone");
        }

        return changed;
    }

    /// <summary>
    /// Which of the listed mods an outfit is wearing, decided by the items in it rather than by
    /// anything having to be paired up by hand: a mod says which items it changes, a design says
    /// which items it puts on, and the overlap is the answer. The mod with the most of them wins
    /// if an outfit borrows a piece from more than one.
    /// </summary>
    private string? OwnerOf(Guid design)
    {
        owners ??= Build();

        var votes = new Dictionary<string, int>();
        foreach (var (_, itemId) in dyes.ItemsOf(design))
        {
            if (!owners.TryGetValue(itemId, out var mods))
                continue;

            foreach (var mod in mods)
                votes[mod] = votes.GetValueOrDefault(mod) + 1;
        }

        return votes.Count == 0 ? null : votes.MaxBy(v => v.Value).Key;
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
        foreach (var mod in config.ExclusiveMods)
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

    public void Release(int objectIndex)
    {
        // The temporary settings come off with everything else this plugin set on them, so
        // there is only the bookkeeping to drop here.
        applied.Remove(objectIndex);
    }

    public void Sweep(HashSet<int> present)
    {
        foreach (var index in applied.Keys.Where(i => !present.Contains(i)).ToList())
            applied.Remove(index);
    }
}
