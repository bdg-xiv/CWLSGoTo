using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class QuestClassJobRewardExtensions {
    extension(QuestClassJobReward) {
        public static List<RowRef<Item>> GetRelicsByRow(int row)
            => QuestClassJobReward.TryGetSubrows((uint)row, out var subrows)
                ? [.. subrows.SelectMany(q => q.RewardItem.TakeWhile(r => r.RowId != 0).Select(r => Item.GetRowRef(r.RowId)))]
                : [];
    }
}
