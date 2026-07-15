using clib.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace clib.Extensions;

public static unsafe class PvPSeriesExtensions {
    private static readonly Lazy<Dictionary<uint, Collection<RowRef<Item>>>> AttireItemsCache = new(BuildAttireItemsCache, isThreadSafe: true);
    private static Dictionary<uint, Collection<RowRef<Item>>> BuildAttireItemsCache() {
        var cache = new Dictionary<uint, Collection<RowRef<Item>>>();
        foreach (var line in Svc.Data.GetSupplemental<ItemSupplement>(CsvLoader.ItemSupplementResourceName).Where(r => r.ItemSupplementSource is ItemSupplementSource.Loot)) {
            if (!MirageStoreSetItemLookup.TryGetRow(line.ItemId, out var lookup)) continue;
            if (!MirageStoreSetItem.TryGetRow(lookup.Item[0].RowId, out var mirage)) continue;

            if (!cache.ContainsKey(line.SourceItemId))
                cache[line.SourceItemId] = mirage.Items;
        }
        return cache;
    }

    extension(PvPSeries row) {
        public Collection<RowRef<Item>> AttireItems => AttireItemsCache.Value.TryGetValue(row.LevelRewards[25].LevelRewardItem[0].RowId, out var items) ? items : [];
    }
}
