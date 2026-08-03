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

    private byte[]? palette;

    /// <summary>Every real dye in the game. Row 0 is "no dye" and is not one.</summary>
    private byte[] Palette()
    {
        if (palette != null)
            return palette;

        var sheet = Svc.Data.GetExcelSheet<Stain>();
        palette = sheet == null
            ? []
            : sheet.Where(s => s.RowId is > 0 and < 256 && s.Name.ExtractText().Length > 0)
                   .Select(s => (byte)s.RowId)
                   .ToArray();

        Svc.Log.Information($"[GlamRoulette] {palette.Length} dyes available");
        return palette;
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

        var dyes = Palette();
        if (dyes.Length == 0)
            return;

        // One colour per channel for the whole outfit, not per slot. Rolling every slot
        // separately produced a harlequin; a single pair reads as an outfit someone dyed.
        // The two channels are rolled independently, so they can land on the same colour
        // by chance, which is fine - that is a plain single-dyed outfit.
        var first = dyes[(int)(Seed(playerKey, design, 0) % (uint)dyes.Length)];
        var second = config.DyeSecondChannel
            ? dyes[(int)(Seed(playerKey, design, 1) % (uint)dyes.Length)]
            : first;

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
