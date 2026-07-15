using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class FateExtensions {
    extension(Fate fate) {
        public bool HasFollowUp => fate.GetFollowUp() is { };

        /// <remarks>This is a very basic heuristic. I know there's some that don't follow this (the two at the top of Outer La Noscea).</summary>
        public Fate? GetFollowUp()
            => Fate.FirstOrNull(row => row.RowId > fate.RowId && row.Location == fate.Location);
    }
}
