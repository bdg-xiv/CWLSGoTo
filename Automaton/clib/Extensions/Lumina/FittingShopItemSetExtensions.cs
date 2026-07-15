using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class FittingShopItemSetExtensions {
    extension(FittingShopItemSet row) {
        public unsafe Collection<RowRef<Item>> Items => new(row.ExcelPage, parentOffset: row.RowOffset, offset: row.RowOffset, &ItemCtor, size: 5);
    }

    internal static RowRef<Item> ItemCtor(ExcelPage page, uint parentOffset, uint offset, uint i)
        => new(page.Module, page.ReadUInt32(offset + i * 4), page.Language);
}
