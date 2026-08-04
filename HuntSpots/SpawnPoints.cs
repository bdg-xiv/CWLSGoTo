using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace HuntSpots;

internal readonly record struct SpawnPoint(Vector2 World, bool A, bool B, bool S)
{
    public bool Wants(Rank rank) => rank switch
    {
        Rank.S => S,
        Rank.A => A,
        _ => B,
    };
}

internal enum Rank
{
    S,
    A,
    B,
}

/// <summary>
/// Where marks come up, by map. The spawn points themselves are not in the game files - nothing
/// in the sheets says where a mark can appear - so they are the ones the community mapped out,
/// taken from Hunt Helper (github.com/img02/HuntHelper, MIT) and used with thanks.
///
/// The file stores them as map coordinates, the numbers the game prints in a flag. Markers want
/// world positions, so they are converted once on load using the map's own scale and offset.
/// </summary>
internal static class SpawnPoints
{
    private const string Resource = "HuntSpots.Data.SpawnPointData.json";

    /// <summary>Keyed by Map row, since that is what the open map reports about itself.</summary>
    private static Dictionary<uint, List<SpawnPoint>>? byMap;
    private static Dictionary<uint, string>? names;

    public static int Zones => Load().Count;

    public static IReadOnlyList<SpawnPoint> For(uint mapId)
        => Load().TryGetValue(mapId, out var points) ? points : [];

    public static string NameOf(uint mapId)
    {
        Load();
        return names!.TryGetValue(mapId, out var name) ? name : $"map #{mapId}";
    }

    private static Dictionary<uint, List<SpawnPoint>> Load()
    {
        if (byMap != null)
            return byMap;

        byMap = [];
        names = [];

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
            if (stream == null)
            {
                Svc.Log.Error($"[HuntSpots] {Resource} is missing from the assembly");
                return byMap;
            }

            using var reader = new StreamReader(stream);
            var zones = JArray.Parse(reader.ReadToEnd());
            var territories = Svc.Data.GetExcelSheet<TerritoryType>();

            foreach (var zone in zones)
            {
                // The file's "MapID" is a territory, not a map - the two numbering schemes
                // overlap, so reading it as a map id silently lands you in a random dungeon.
                var territoryId = zone["MapID"]?.ToObject<uint>() ?? 0;
                if (territoryId == 0 || !territories.TryGetRow(territoryId, out var territory))
                    continue;

                if (territory.Map.ValueNullable is not { } map || map.RowId == 0)
                    continue;

                var points = new List<SpawnPoint>();
                foreach (var entry in zone["Positions"] ?? new JArray())
                {
                    var x = entry["X"]?.ToObject<float>();
                    var y = entry["Y"]?.ToObject<float>();
                    if (x is null || y is null)
                        continue;

                    points.Add(new SpawnPoint(
                        new Vector2(ToWorld(x.Value, map.SizeFactor, map.OffsetX),
                                    ToWorld(y.Value, map.SizeFactor, map.OffsetY)),
                        entry["A"]?.ToObject<bool>() ?? false,
                        entry["B"]?.ToObject<bool>() ?? false,
                        entry["S"]?.ToObject<bool>() ?? false));
                }

                if (points.Count == 0)
                    continue;

                byMap[map.RowId] = points;
                names[map.RowId] = zone["MapName"]?.ToObject<string>() ?? $"map #{map.RowId}";
            }

            Svc.Log.Information($"[HuntSpots] {byMap.Count} zones of spawn points loaded");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to read the spawn points");
        }

        return byMap;
    }

    /// <summary>
    /// A map coordinate back to the world position it names. The game's own conversion the
    /// other way is scaledPos = (world + offset) * scale, then map = (scaledPos + 1024) / 2048
    /// * 41 / scale + 1; this is that solved for the world position.
    /// </summary>
    private static float ToWorld(float mapCoord, ushort sizeFactor, short offset)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) / 100f;
        return ((mapCoord - 1f) * scale * 2048f / 41f - 1024f) / scale - offset;
    }
}
