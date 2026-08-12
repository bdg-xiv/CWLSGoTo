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

    private (byte Id, string Name, Tier Tier)[]? palette;

    /// <summary>Every dye, for the window to list and weight one at a time.</summary>
    public IReadOnlyList<(byte Id, string Name, Tier Tier)> All => Palette();

    /// <summary>
    /// Every real dye, sorted into how hard it is to come by. Metallic is the game's own
    /// IsMetallic column rather than a list of names, so it stays right as dyes are added.
    /// Premium is the 668-gil tier - the pastels, darks, Pure White and Jet Black - picked out
    /// by the price of the item that applies them, plus anything with no vendor item at all.
    /// </summary>
    private (byte Id, string Name, Tier Tier)[] Palette()
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
                return ((byte)s.RowId, name, tier);
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

    /// <summary>Which set of dials answers - the shared ones, or your own when your rolls
    /// have been given odds of their own.</summary>
    private bool Mine(bool mine) => mine && config.SeparateMyOdds;

    private Dictionary<uint, int> Named(bool mine)
        => Mine(mine) ? config.MyDyeWeights : config.DyeWeights;

    private int TierWeight(Tier tier, bool mine = false) => Math.Max(0, tier switch
    {
        Tier.Metallic => Mine(mine) ? config.MyMetallicWeight : config.MetallicWeight,
        Tier.Premium => Mine(mine) ? config.MyPremiumWeight : config.PremiumWeight,
        _ => Mine(mine) ? config.MyStandardWeight : config.StandardWeight,
    });

    /// <summary>
    /// How often one dye comes up. A dye named in the config answers for itself and its tier no
    /// longer speaks for it - which is what "this one twice as often" and "never this one" both
    /// need, and either would be impossible while a tier was the smallest thing there was.
    /// </summary>
    public int WeightOf(byte id, bool mine = false)
        => Named(mine).TryGetValue(id, out var named)
            ? Math.Max(0, named)
            : TierWeight(Palette().FirstOrDefault(p => p.Id == id).Tier, mine);

    private int WeightOf((byte Id, string Name, Tier Tier) dye, bool mine)
        => Named(mine).TryGetValue(dye.Id, out var named) ? Math.Max(0, named) : TierWeight(dye.Tier, mine);

    /// <summary>Whether this dye has been given a weight of its own.</summary>
    public bool IsNamed(byte id, bool mine = false) => Named(mine).ContainsKey(id);

    /// <summary>The share of rolls each tier will take, for showing in the settings.</summary>
    public float Share(Tier tier, bool mine = false)
    {
        var total = Palette().Sum(p => (long)WeightOf(p, mine));
        if (total == 0)
            return 0f;

        return (float)Palette().Where(p => p.Tier == tier).Sum(p => (long)WeightOf(p, mine)) / total;
    }

    /// <summary>The share of rolls one particular dye will take.</summary>
    public float ShareOf(byte id, bool mine = false)
    {
        var total = Palette().Sum(p => (long)WeightOf(p, mine));
        return total == 0 ? 0f : (float)WeightOf(id, mine) / total;
    }

    /// <summary>
    /// Picks a dye with the tiers weighted, from a number that is derived rather than random,
    /// so the same wearer keeps the same colour.
    /// </summary>
    private byte Pick(uint seed, bool mine)
    {
        var dyes = Palette();
        var total = dyes.Sum(p => (long)WeightOf(p, mine));

        // Everything weighted to nothing would be a division by zero, and "no dyes at all" is
        // not what someone means by turning every weight down.
        if (total <= 0)
            return dyes[(int)(seed % (uint)dyes.Length)].Id;

        var target = (long)(seed % (uint)total);
        foreach (var dye in dyes)
        {
            target -= WeightOf(dye, mine);
            if (target < 0)
                return dye.Id;
        }

        return dyes[^1].Id;
    }

    public List<(byte Slot, ulong ItemId)> ItemsOf(Guid design)
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

    /// <summary>
    /// What an outfit actually puts on somebody: the design's own items, with the feet swapped
    /// for a rolled pair when they have been given one. Everything that asks what an outfit is
    /// wearing goes through here - the dyeing, which mods it wants rolled, and which of a
    /// clashing pair it belongs to - so a rolled pair of shoes counts as worn everywhere rather
    /// than only where it is put on.
    /// </summary>
    public List<(byte Slot, ulong ItemId)> ItemsWorn(Guid design, uint? shoe)
    {
        var worn = ItemsOf(design);
        if (shoe is not { } item)
            return worn;

        var swapped = worn.Where(w => w.Slot != Feet).ToList();
        swapped.Add((Feet, item));
        return swapped;
    }

    private const byte Feet = 8;

    /// <summary>Dyes an outfit that has just been applied, and puts the rolled shoes on with
    /// the same call - a slot is set by naming its item, which is what dyeing does anyway.</summary>
    public void Apply(int objectIndex, string playerKey, Guid design, int roll, uint? shoe, bool mine = false)
    {
        // The shoes still have to go on even when nothing is being re-dyed, so this is not a
        // dye-only pass any more.
        if (shoe is { } only && (!config.RandomizeDyes || Palette().Length == 0))
        {
            glamourer.Dye(objectIndex, Feet, only, [0, 0]);
            return;
        }

        if (!config.RandomizeDyes)
            return;

        if (Palette().Length == 0)
            return;

        // One colour per channel for the whole outfit, not per slot. Rolling every slot
        // separately produced a harlequin; a single pair reads as an outfit someone dyed.
        // The two channels are rolled independently, so they can land on the same colour
        // by chance, which is fine - that is a plain single-dyed outfit.
        var first = Pick(Seed(playerKey, design, roll, 0), mine);
        var second = config.DyeSecondChannel ? Pick(Seed(playerKey, design, roll, 1), mine) : first;

        foreach (var (slot, itemId) in ItemsWorn(design, shoe))
            glamourer.Dye(objectIndex, slot, itemId, [first, second]);
    }

    /// <summary>
    /// The same person in the same outfit has to come out the same colour every time, so the
    /// dye is derived rather than drawn. String.GetHashCode is randomised per process and
    /// would give someone a new palette on every restart, hence the hand-rolled one.
    /// </summary>
    private static uint Seed(string playerKey, Guid design, int roll, byte channel)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in playerKey)
                hash = (hash ^ c) * 16777619u;

            foreach (var b in design.ToByteArray())
                hash = (hash ^ b) * 16777619u;

            foreach (var b in BitConverter.GetBytes(roll))
                hash = (hash ^ b) * 16777619u;

            hash = (hash ^ channel) * 16777619u;
            return hash;
        }
    }
}
