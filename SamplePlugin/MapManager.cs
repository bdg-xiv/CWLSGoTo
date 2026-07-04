using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace SamplePlugin;

internal static class MapManager
{
    // Matches a public world's name anywhere in the message text, regardless of case
    // (e.g. "in DIABOLOS", "Diabolos", "diabolos" should all match the "Diabolos" world).
    internal static World? ParseWorldFromText(string text)
    {
        foreach (var world in Svc.Data.GetExcelSheet<World>())
        {
            if (!world.IsPublic)
                continue;

            var name = world.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;

            if (Regex.IsMatch(text, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                return world;
        }

        return null;
    }

    internal static Aetheryte? GetNearestAetheryte(MapLinkPayload mapLink)
    {
        var territoryId = mapLink.TerritoryType.RowId;

        Map? map = null;
        foreach (var m in Svc.Data.GetExcelSheet<Map>())
        {
            if (m.TerritoryType.RowId == territoryId)
            {
                map = m;
                break;
            }
        }
        if (map == null)
            return null;

        var scale = map.Value.SizeFactor;
        var markerSheet = Svc.Data.GetSubrowExcelSheet<MapMarker>();

        Aetheryte? nearest = null;
        var nearestDistanceSq = double.MaxValue;

        foreach (var aetheryte in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte || aetheryte.Territory.RowId != territoryId)
                continue;

            MapMarker? marker = null;
            foreach (var m in markerSheet.Flatten())
            {
                if (m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId)
                {
                    marker = m;
                    break;
                }
            }
            if (marker == null)
                continue;

            var x = ConvertMapMarkerToMapCoordinate(marker.Value.X, scale);
            var y = ConvertMapMarkerToMapCoordinate(marker.Value.Y, scale);
            var distanceSq = Math.Pow(x - mapLink.XCoord, 2) + Math.Pow(y - mapLink.YCoord, 2);
            if (distanceSq < nearestDistanceSq)
            {
                nearestDistanceSq = distanceSq;
                nearest = aetheryte;
            }
        }

        return nearest;
    }

    private static float ConvertMapMarkerToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        var rawPosition = (int)((pos - 1024.0) / num * 1000f);
        return ConvertRawPositionToMapCoordinate(rawPosition, scale);
    }

    private static float ConvertRawPositionToMapCoordinate(int pos, float scale)
    {
        var num = scale / 100f;
        return (float)((pos / 1000f * num + 1024.0) / 2048.0 * 41.0 / num + 1.0);
    }
}
