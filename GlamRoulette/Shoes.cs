using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace GlamRoulette;

/// <summary>
/// Deals out a pair of shoes in place of the ones a design specifies.
///
/// Opt-in per design, deliberately. On most outfits the shoes are part of the outfit and
/// swapping them is vandalism; on the handful built around a bare leg they are the one piece
/// worth varying, and varying them there beats keeping a near-identical design per pair.
/// </summary>
internal sealed class Shoes(Configuration config)
{
    private (uint Id, string Name)[]? catalogue;

    /// <summary>Every item that goes on the feet, for picking a pool out of.</summary>
    public IReadOnlyList<(uint Id, string Name)> Catalogue()
    {
        if (catalogue != null)
            return catalogue;

        var items = Svc.Data.GetExcelSheet<Item>();
        if (items == null)
            return catalogue = [];

        catalogue = items
            .Where(i => i.ModelMain != 0 && i.EquipSlotCategory.ValueNullable?.Feet > 0)
            .Select(i => (i.RowId, Name: i.Name.ExtractText()))
            .Where(i => i.Name.Length > 0)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return catalogue;
    }

    public string NameOf(uint item)
        => Catalogue().FirstOrDefault(c => c.Id == item).Name is { Length: > 0 } name
            ? name
            : $"#{item}";

    /// <summary>
    /// The shoes this wearer gets for this outfit, or nothing when the outfit keeps its own.
    /// Derived rather than drawn, like the dyes, so the same person keeps the same pair until
    /// they are re-rolled - and the roll counter is in the sum, so a re-roll changes them.
    /// </summary>
    public uint? For(string playerKey, Guid design, int roll)
    {
        if (!config.RollShoes || config.ShoePool.Count == 0 || !config.RollShoesFor.Contains(design))
            return null;

        var pool = config.ShoePool;
        return pool[(int)(Seed(playerKey, design, roll) % (uint)pool.Count)];
    }

    private static uint Seed(string playerKey, Guid design, int roll)
    {
        unchecked
        {
            // Offset from the dyes' basis so a pair of shoes is not tied to a colour.
            var hash = 2166136261u ^ 0x5EED;

            foreach (var c in playerKey)
                hash = (hash ^ c) * 16777619u;
            foreach (var b in design.ToByteArray())
                hash = (hash ^ b) * 16777619u;
            foreach (var b in BitConverter.GetBytes(roll))
                hash = (hash ^ b) * 16777619u;

            return hash;
        }
    }
}
