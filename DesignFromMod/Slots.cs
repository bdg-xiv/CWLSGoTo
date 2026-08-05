using System.Collections.Generic;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace DesignFromMod;

/// <summary>
/// Which slot an item goes in, in the words a Glamourer design uses. Read off the game's own
/// EquipSlotCategory rather than a table of item ids: every row has a column per slot holding
/// one for "goes here", so the answer is already in the sheet and stays right for items that
/// do not exist yet.
/// </summary>
internal static class Slots
{
    /// <summary>Glamourer's names, in the order a design lists them.</summary>
    public static readonly string[] Order =
    [
        "MainHand", "OffHand", "Head", "Body", "Hands", "Legs", "Feet",
        "Ears", "Neck", "Wrists", "RFinger", "LFinger",
    ];

    public static string? Of(in Item item)
    {
        if (item.EquipSlotCategory.ValueNullable is not { } category)
            return null;

        // Waist is deliberately absent: the game stopped drawing belts, and Glamourer has no
        // slot to put one in.
        if (category.MainHand == 1) return "MainHand";
        if (category.OffHand == 1) return "OffHand";
        if (category.Head == 1) return "Head";
        if (category.Body == 1) return "Body";
        if (category.Gloves == 1) return "Hands";
        if (category.Legs == 1) return "Legs";
        if (category.Feet == 1) return "Feet";
        if (category.Ears == 1) return "Ears";
        if (category.Neck == 1) return "Neck";
        if (category.Wrists == 1) return "Wrists";
        if (category.FingerR == 1) return "RFinger";
        if (category.FingerL == 1) return "LFinger";

        return null;
    }

    /// <summary>
    /// Every equippable item by name. Penumbra reports what a mod changes by name, and names
    /// repeat across the sheet - a glamour prism has the name of the gear it holds - so the
    /// first one that can actually be worn is the one meant.
    /// </summary>
    public static IReadOnlyDictionary<string, (uint Id, string Slot)> ByName()
    {
        var found = new Dictionary<string, (uint, string)>();

        foreach (var item in Svc.Data.GetExcelSheet<Item>())
        {
            var name = item.Name.ExtractText();
            if (name.Length == 0 || found.ContainsKey(name))
                continue;

            if (Of(item) is { } slot)
                found[name] = (item.RowId, slot);
        }

        return found;
    }
}
