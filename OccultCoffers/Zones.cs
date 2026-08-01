using System.Collections.Generic;
using System.Numerics;

namespace OccultCoffers;

/// <summary>
/// The Occult Crescent zones. North Horn is one territory but two maps - the North Basin
/// above and the Subterrane below - and Treasuresight counts the coffers on both at once,
/// so spots from both floors live in the same pool and only the marker's map id separates
/// them.
/// </summary>
internal static class Zones
{
    internal sealed class ZoneInfo
    {
        public required uint TerritoryId;
        public required string Name;
        public required uint SurfaceMapId;
        public required string SurfaceName;
        public uint SubterraneMapId;
        public string SubterraneName = string.Empty;

        public bool HasSubterrane => SubterraneMapId != 0;

        public uint MapIdFor(Vector3 world, float subterraneCeilingY)
            => HasSubterrane && world.Y <= subterraneCeilingY ? SubterraneMapId : SurfaceMapId;

        public string FloorNameFor(uint mapId)
            => mapId == SubterraneMapId ? SubterraneName : SurfaceName;
    }

    public static readonly Dictionary<uint, ZoneInfo> ByTerritory = new()
    {
        [1252] = new ZoneInfo
        {
            TerritoryId = 1252,
            Name = "South Horn",
            SurfaceMapId = 967,
            SurfaceName = "South Horn",
        },
        [1346] = new ZoneInfo
        {
            TerritoryId = 1346,
            Name = "North Horn",
            SurfaceMapId = 1135,
            SurfaceName = "North Basin",
            SubterraneMapId = 1244,
            SubterraneName = "Subterrane",
        },
    };

    public static ZoneInfo? For(uint territoryId)
        => ByTerritory.TryGetValue(territoryId, out var zone) ? zone : null;
}
