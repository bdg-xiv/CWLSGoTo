using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class QuestExtensions {
    extension(Quest row) {
        public List<Level> TodoLevels => [.. row.TodoParams.SelectMany(param => param.ToDoLocation).Where(rowRef => rowRef.RowId != 0).Select(rowRef => rowRef.Value)];
    }
}
