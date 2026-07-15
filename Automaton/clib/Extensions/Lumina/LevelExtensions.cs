using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class LevelExtensions {
    public static Vector3 ToVector3(this Level row) => new(row.X, row.Y, row.Z);
}
