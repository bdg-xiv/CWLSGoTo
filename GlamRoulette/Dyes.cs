using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace GlamRoulette;

/// <summary>
/// Re-dyes an outfit after it has been put on: one colour for channel one and one for
/// channel two, the same across every slot. Glamourer has no "change the dye" call, so each
/// slot is set to the item it already has with different stains attached.
/// </summary>
internal sealed class Dyes(Configuration config, GlamourerIpc glamourer)
{
    /// <summary>Slot names as a design writes them, against the API's slot numbers.</summary>
    private static readonly (string Name, byte Slot)[] Slots =
    [
        ("Head", 3), ("Body", 4), ("Hands", 5), ("Legs", 7), ("Feet", 8),
        ("Ears", 9), ("Neck", 10), ("Wrists", 11), ("RFinger", 12), ("LFinger", 14),
        ("MainHand", 1), ("OffHand", 2),
    ];

    /// <summary>Item ids per slot, read out of the design once and kept.</summary>
    private readonly Dictionary<Guid, List<(byte Slot, ulong ItemId)>> items = [];

    public enum Tier
    {
        Standard,
        Premium,
        Metallic,
    }

    private (byte Id, Tier Tier)[]? palette;

    /// <summary>
    /// Every real dye, sorted into how hard it is to come by. Metallic is the game's own
    /// IsMetallic column rather than a list of names, so it stays right as dyes are added.
    /// Premium is the 668-gil tier - the pastels, darks, Pure White and Jet Black - picked out
    /// by the price of the item that applies them, plus anything with no vendor item at all.
    /// </summary>
    private (byte Id, Tier Tier)[] Palette()
    {
        if (palette != null)
            return palette;

        var stains = Svc.Data.GetExcelSheet<Stain>();
        if (stains == null)
            return palette = [];

        var prices = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var items = Svc.Data.GetExcelSheet<Item>();
        if (items != null)
        {
            foreach (var item in items)
            {
                var name = item.Name.ExtractText();
                if (name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase))
                    prices.TryAdd(name[..^4].Trim(), item.PriceMid);
            }
        }

        palette = stains
            .Where(s => s.RowId is > 0 and < 256 && s.Name.ExtractText().Length > 0)
            .Select(s =>
            {
                var name = s.Name.ExtractText().Trim();
                var tier = s.IsMetallic ? Tier.Metallic
                    : !prices.TryGetValue(name, out var price) || price > PremiumPrice ? Tier.Premium
                    : Tier.Standard;
                return ((byte)s.RowId, tier);
            })
            .ToArray();

        Svc.Log.Information($"[GlamRoulette] {palette.Length} dyes: " +
                            $"{palette.Count(p => p.Tier == Tier.Metallic)} metallic, " +
                            $"{palette.Count(p => p.Tier == Tier.Premium)} premium, " +
                            $"{palette.Count(p => p.Tier == Tier.Standard)} standard");
        return palette;
    }

    private const uint PremiumPrice = 400;

    public int Count(Tier tier) => Palette().Count(p => p.Tier == tier);

    private int WeightOf(Tier tier) => Math.Max(0, tier switch
    {
        Tier.Metallic => config.MetallicWeight,
        Tier.Premium => config.PremiumWeight,
        _ => config.StandardWeight,
    });

    /// <summary>The share of rolls each tier will take, for showing in the settings.</summary>
    public float Share(Tier tier)
    {
        var total = Palette().Sum(p => (long)WeightOf(p.Tier));
        if (total == 0)
            return 0f;

        return (float)Palette().Where(p => p.Tier == tier).Sum(p => (long)WeightOf(p.Tier)) / total;
    }

    /// <summary>
    /// Picks a dye with the tiers weighted, from a number that is derived rather than random,
    /// so the same wearer keeps the same colour.
    /// </summary>
    private byte Pick(uint seed)
    {
        var dyes = Palette();
        var total = dyes.Sum(p => (long)WeightOf(p.Tier));

        // Everything weighted to nothing would be a division by zero, and "no dyes at all" is
        // not what someone means by turning every weight down.
        if (total <= 0)
            return dyes[(int)(seed % (uint)dyes.Length)].Id;

        var target = (long)(seed % (uint)total);
        foreach (var (id, tier) in dyes)
        {
            target -= WeightOf(tier);
            if (target < 0)
                return id;
        }

        return dyes[^1].Id;
    }

    private List<(byte Slot, ulong ItemId)> ItemsOf(Guid design)
    {
        if (items.TryGetValue(design, out var cached))
            return cached;

        var found = new List<(byte, ulong)>();
        var json = glamourer.Design(design);
        var equipment = json?["Equipment"];

        if (equipment != null)
        {
            foreach (var (name, slot) in Slots)
            {
                var entry = equipment[name];
                if (entry == null)
                    continue;

                // Only slots the design actually sets - dyeing a slot it leaves alone would
                // be us changing something the design deliberately did not touch.
                if (entry["Apply"]?.ToObject<bool>() != true)
                    continue;

                var itemId = entry["ItemId"]?.ToObject<ulong>();
                if (itemId is null or 0)
                    continue;

                found.Add((slot, itemId.Value));
            }
        }

        items[design] = found;
        return found;
    }

    public void Forget() => items.Clear();

    /// <summary>Dyes an outfit that has just been applied.</summary>
    public void Apply(int objectIndex, string playerKey, Guid design)
    {
        if (!config.RandomizeDyes)
            return;

        if (Palette().Length == 0)
            return;

        // One colour per channel for the whole outfit, not per slot. Rolling every slot
        // separately produced a harlequin; a single pair reads as an outfit someone dyed.
        // The two channels are rolled independently, so they can land on the same colour
        // by chance, which is fine - that is a plain single-dyed outfit.
        var first = Pick(Seed(playerKey, design, 0));
        var second = config.DyeSecondChannel ? Pick(Seed(playerKey, design, 1)) : first;

        foreach (var (slot, itemId) in ItemsOf(design))
            glamourer.Dye(objectIndex, slot, itemId, [first, second]);
    }

    /// <summary>
    /// The same person in the same outfit has to come out the same colour every time, so the
    /// dye is derived rather than drawn. String.GetHashCode is randomised per process and
    /// would give someone a new palette on every restart, hence the hand-rolled one.
    /// </summary>
    private static uint Seed(string playerKey, Guid design, byte channel)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in playerKey)
                hash = (hash ^ c) * 16777619u;

            foreach (var b in design.ToByteArray())
                hash = (hash ^ b) * 16777619u;

            hash = (hash ^ channel) * 16777619u;
            return hash;
        }
    }
}
