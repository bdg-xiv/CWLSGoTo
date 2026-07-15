using clib.Services;
using Lumina.Excel;
using Lumina.Extensions;

namespace clib.Extensions;

public static class IExcelRowExtensions {
    extension<T>(IExcelRow<T> excelRow) where T : struct, IExcelRow<T> {
        public T WithLanguage(Dalamud.Game.ClientLanguage language)
            => Svc.Data.GetExcelSheet<T>(language: language).GetRow(excelRow.RowId);

        public T WithLanguage(Lumina.Data.Language language)
            => Svc.Data.GetExcelSheet<T>(language: (Dalamud.Game.ClientLanguage)language).GetRow(excelRow.RowId);

        public static IEnumerable<T> Rows => Svc.Data.GetExcelSheet<T>();

        public static RowRef<T> GetRowRef(uint id, Lumina.Data.Language? language = null)
            => new(Svc.Data.Excel, id, language);

        public static T GetRow(uint id, Dalamud.Game.ClientLanguage? language = null)
            => Svc.Data.GetExcelSheet<T>(language: language).GetRow(id);

        public static bool TryGetRow(uint id, out T row, Dalamud.Game.ClientLanguage? language = null) {
            if (Svc.Data.GetExcelSheet<T>(language: language).TryGetRow(id, out var r)) {
                row = r;
                return true;

            }
            else {
                row = default;
                return false;
            }
        }

        public static bool Any(Func<T, bool> predicate)
            => Svc.Data.GetExcelSheet<T>().Any(r => predicate(r));

        public static int Count(Func<T, bool> predicate)
            => Svc.Data.GetExcelSheet<T>().Count(r => predicate(r));

        public static bool All(Func<T, bool> predicate)
            => Svc.Data.GetExcelSheet<T>().All(r => predicate(r));

        public static T[] Where(Func<T, bool> predicate)
            => [.. Svc.Data.GetExcelSheet<T>().Where(r => predicate(r))];

        public static TResult[] Select<TResult>(Func<T, TResult> selector)
            => [.. Svc.Data.GetExcelSheet<T>().Select(selector)];

        public static T? FirstOrNull()
            => Svc.Data.GetExcelSheet<T>().FirstOrNull();

        public static T? FirstOrNull(Func<T, bool> predicate)
            => Svc.Data.GetExcelSheet<T>().Where(r => predicate(r)).FirstOrNull();
    }
}

public static class IExcelSubrowExtensions {
    extension<T>(IExcelSubrow<T> row) where T : struct, IExcelSubrow<T> {
        public T? WithLanguage(ushort subRowId, Dalamud.Game.ClientLanguage language)
            => Svc.Data.GetSubrowSheet<T>(language: language).GetSubrowOrDefault(row.RowId, subRowId);

        public T? WithLanguage(ushort subRowId, Lumina.Data.Language language)
            => Svc.Data.GetSubrowSheet<T>(language: (Dalamud.Game.ClientLanguage)language).GetSubrowOrDefault(row.RowId, subRowId);

        public static IEnumerable<T> Rows => Svc.Data.GetSubrowSheet<T>().SelectMany(r => r);

        public static SubrowRef<T> GetSubrowRef(uint rowId, Lumina.Data.Language? language = null)
            => new(Svc.Data.Excel, rowId, language);

        public static T? GetSubrow(uint rowId, ushort subRowId, Dalamud.Game.ClientLanguage? language = null)
            => Svc.Data.GetSubrowSheet<T>(language: language).GetSubrowOrDefault(rowId, subRowId);

        public static bool TryGetSubrow(uint rowId, ushort subRowId, out T subrow, Dalamud.Game.ClientLanguage? language = null) {
            if (Svc.Data.GetSubrowSheet<T>(language: language).TryGetSubrow(rowId, subRowId, out var r)) {
                subrow = r;
                return true;
            }
            else {
                subrow = default;
                return false;
            }
        }

        public static bool TryGetSubrows(uint rowId, out SubrowCollection<T> subrows) {
            if (Svc.Data.TryGetSubrows<T>(rowId, out var r)) {
                subrows = r;
                return true;
            }
            else {
                subrows = [];
                return false;
            }
        }

        public static bool Any(Func<T, bool> predicate)
            => EnumerateSubrows<T>().Any(predicate);

        public static int Count(Func<T, bool> predicate)
            => EnumerateSubrows<T>().Count(predicate);

        public static bool All(Func<T, bool> predicate)
            => EnumerateSubrows<T>().All(predicate);

        public static T[] Where(Func<T, bool> predicate)
            => [.. EnumerateSubrows<T>().Where(predicate)];

        public static TResult[] Select<TResult>(Func<T, TResult> selector)
            => [.. EnumerateSubrows<T>().Select(selector)];

        public static T? FirstOrNull()
            => EnumerateSubrows<T>().FirstOrNull();

        public static T? FirstOrNull(Func<T, bool> predicate)
            => EnumerateSubrows<T>().Where(predicate).FirstOrNull();
    }

    private static IEnumerable<T> EnumerateSubrows<T>(Dalamud.Game.ClientLanguage? language = null) where T : struct, IExcelSubrow<T>
        => Svc.Data.GetSubrowSheet<T>(language: language).SelectMany(r => r);
}
