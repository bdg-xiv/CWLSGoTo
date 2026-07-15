using clib.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Extensions;

namespace clib.Utils;

public static class Coords {
    public static Vector3 PixelCoordsToWorldCoords(int x, int z, uint mapId) {
        var map = Svc.Data.GetRef<Sheets.Map>(mapId);
        var scale = map.Value.SizeFactor * 0.01f;
        var wx = PixelCoordToWorldCoord(x, scale, map.Value.OffsetX);
        var wz = PixelCoordToWorldCoord(z, scale, map.Value.OffsetY);
        return new(wx, 0, wz);
    }

    // see: https://github.com/xivapi/ffxiv-datamining/blob/master/docs/MapCoordinates.md
    // see: dalamud MapLinkPayload class
    public static float PixelCoordToWorldCoord(float coord, float scale, short offset) {
        // +1 - networkAdjustment == 0
        // (coord / scale * 2) * (scale / 100) = coord / 50
        // * 2048 / 41 / 50 = 0.999024
        const float factor = 2048.0f / (50 * 41);
        return (coord * factor - 1024f) / scale - offset * 0.001f;
    }

    public static uint? FindClosestAetheryteToFlag(bool includeAethernet = true)
        => FlagMapMarker.Get() is { } flag ? FindClosestAetheryte(flag.TerritoryId, flag.Position.ToVector3(), includeAethernet) : null;

    public static uint? FindClosestAetheryte(uint territoryTypeId, Vector3 worldPos, bool includeAethernet = true) {
        if (!includeAethernet && territoryTypeId == 886) // Firmament
            return 70; // Ishgard
        if (!includeAethernet && territoryTypeId == 478) // Hinterlands
            return 75; // Idyllshire
        List<Sheets.Aetheryte> aetherytes = [.. Svc.Data.GetExcelSheet<Sheets.Aetheryte>().Where(a => a.Territory.RowId == territoryTypeId && (includeAethernet || a.IsAetheryte)) ?? []];
        // aetherytes tend to not have a Y whereas gates do. Maps are mostly flat so just equalise and ignore Y
        var validAetherytes = aetherytes.Select(a => (a.RowId, Pos: AetherytePosition(a))).Where(x => x.Pos.IsFinite()).ToList();
        return validAetherytes.Count > 0 ? validAetherytes.MinBy(x => (worldPos.ToVector2() - x.Pos.ToVector2()).LengthSquared()).RowId : null;
    }

    public static Vector3 AetherytePosition(uint aetheryteId) => AetherytePosition(Svc.Data.GetRef<Sheets.Aetheryte>(aetheryteId).Value);
    public static Vector3 AetherytePosition(Sheets.Aetheryte a) {
        // stolen from HTA, uses pixel coordinates
        if (a.Level[0].ValueNullable is { } l)
            return new(l.X, l.Y, l.Z);
        var marker = Svc.Data.GetSubrowExcelSheet<Sheets.MapMarker>().SelectMany(x => x)
            .Where(m => m.DataType == 3 && m.DataKey.RowId == a.RowId || m.DataType == 4 && m.DataKey.RowId == a.AethernetName.RowId)
            .FirstOrNull();
        return marker != null ? PixelCoordsToWorldCoords(marker.Value.X, marker.Value.Y, a.Territory.Value.Map.RowId) : Vector3.NaN;
    }

    public static bool IsTeleportingFaster(Vector3 dest) {
        if (Svc.Objects.LocalPlayer is not { Position: var pos }) return false;
        const int overhead = 300; // approximately the distance you can travel in the time it takes you to teleport
        return FindClosestAetheryte(Svc.ClientState.TerritoryType, dest) is { } closest && overhead + (dest - AetherytePosition(closest)).Length() < (dest - pos).Length();
    }

    // if aetheryte is 'primary' (i.e. can be teleported to), return it; otherwise (i.e. aethernet shard) find and return primary aetheryte from same group
    public static uint FindPrimaryAetheryte(uint aetheryteId) {
        if (aetheryteId == 0)
            return 0;
        var row = Svc.Data.GetRef<Sheets.Aetheryte>(aetheryteId).Value;
        if (row.IsAetheryte)
            return aetheryteId;
        var primary = Svc.Data.GetExcelSheet<Sheets.Aetheryte>().FirstOrNull(a => a.AethernetGroup == row.AethernetGroup && a.IsAetheryte);
        return primary?.RowId ?? 0;
    }

    public static unsafe (ulong id, Vector3 pos) FindAetheryte(uint id) {
        foreach (var obj in GameObjectManager.Instance()->Objects.IndexSorted)
            if (obj.Value != null && obj.Value->ObjectKind == ObjectKind.Aetheryte && obj.Value->BaseId == id)
                return (obj.Value->GetGameObjectId(), *obj.Value->GetPosition());
        return (0, default);
    }
}
