using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;

namespace Laziness;

/// <summary>
/// Reader for the "ShopExchangeItem" addon - the item-for-item exchange window
/// (Bertana's Uncanny Knickknacks). ECommons only ships a master for the
/// currency variant, so the rows are read out of the addon's AtkValues here.
/// </summary>
internal readonly unsafe struct ItemExchangeShop(AtkUnitBase* addon)
{
    // AtkValue block offsets: the addon publishes its rows as parallel arrays.
    private const int EntryCountIndex = 3;
    private const int ItemIdBase = 1066;
    private const int RowIndexBase = 1310;
    private const int CostAmountBase = 2775;
    private const int CostItemIdBase = 3141;
    private const int CostsPerEntry = 3;

    internal readonly AtkUnitBase* Addon = addon;

    internal record Cost(uint ItemId, uint Amount);
    internal record Row(uint ItemId, uint Index, IReadOnlyList<Cost> Costs);

    private uint Value(int index) => Addon->AtkValues[index].UInt;

    internal int EntryCount => (int)Value(EntryCountIndex);

    internal List<Row> Rows()
    {
        var rows = new List<Row>();
        for (var i = 0; i < EntryCount; i++)
        {
            var itemId = Value(ItemIdBase + i);
            if (itemId == 0)
                continue;

            var costs = new List<Cost>();
            for (var j = 0; j < CostsPerEntry; j++)
            {
                var offset = i * CostsPerEntry + j;
                var costItemId = Value(CostItemIdBase + offset);
                if (costItemId != 0)
                    costs.Add(new Cost(costItemId, Value(CostAmountBase + offset)));
            }

            rows.Add(new Row(itemId, Value(RowIndexBase + i), costs));
        }

        return rows;
    }

    /// <summary>Starts the exchange for a row; a confirmation dialog follows.</summary>
    internal void Select(Row row, int amount) => Callback.Fire(Addon, true, 0, row.Index, amount);
}
