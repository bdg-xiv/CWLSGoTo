using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class FishingSpotExtensions {
    extension(FishingSpot row) {
        public Vector2 WorldCoords {
            get {
                var c = row.TerritoryType.Value.Map.Value.SizeFactor / 100.0;
                float ToWorld(float coord) => (float)(41.0f / c * (coord / 2048.0f) + 1f);
                return new Vector2(ToWorld(row.X), ToWorld(row.Z));
            }
        }
    }
}
