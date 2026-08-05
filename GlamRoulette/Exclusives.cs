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

    /// <summary>What each object index was last set to, so nobody is redrawn for no reason.</summary>
    private readonly Dictionary<int, string> applied = [];

    public int Count => applied.Count;

    public void Forget()
    {
        owners = null;
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

        var changed = false;
        foreach (var mod in config.ExclusiveMods)
            changed |= penumbra.Enable(objectIndex, mod.Directory, mod.Directory == wanted);

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

        Svc.Log.Information($"[GlamRoulette] {config.ExclusiveMods.Count} clashing mods cover {built.Count} items");
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
