using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.MapOverlay;

namespace HuntSpots;

/// <summary>
/// Draws the spawn points onto the game's own map, as its own node layer rather than through
/// the shared gathering-marker slots - those are capped at a dozen and are where other plugins'
/// marks already sit.
/// </summary>
internal sealed class MapLayer : IDisposable
{
    private readonly MapOverlayController controller = new();
    private readonly List<MapMarkerNode> markers = [];
    private string drawn = string.Empty;

    public MapLayer() => controller.Enable();

    public void Dispose()
    {
        Clear();
        controller.Dispose();
    }

    public void Clear()
    {
        if (markers.Count == 0 && drawn.Length == 0)
            return;

        controller.RemoveAllMarkers();
        markers.Clear();
        drawn = string.Empty;
    }

    public unsafe void Sync(Configuration config)
    {
        if (!config.Enabled)
        {
            Clear();
            return;
        }

        var agent = AgentMap.Instance();
        if (agent == null)
        {
            Clear();
            return;
        }

        // Whichever map is open, not whichever zone you are in - so the points are there while
        // you are looking at where you are about to go, which is when they are wanted.
        var mapId = config.CurrentZoneOnly ? agent->CurrentMapId : agent->SelectedMapId;
        if (mapId == 0)
        {
            Clear();
            return;
        }

        var wanted = Collect(mapId, config);

        // Rebuilding nodes every frame would be silly; only redo it when something changed.
        var signature = $"{mapId}:{wanted.Count}:{config.IconSize}:{config.SIcon}:{config.AIcon}:{config.BIcon}";
        if (signature == drawn)
            return;

        controller.RemoveAllMarkers();
        markers.Clear();

        foreach (var (point, icon, tooltip) in wanted)
        {
            var marker = new MapMarkerNode
            {
                IconId = icon,
                MapId = mapId,
                Size = new Vector2(config.IconSize, config.IconSize),
                Position = point.World,
                TextTooltip = tooltip,
            };
            markers.Add(marker);
            controller.AddMarker(marker);
        }

        drawn = signature;
    }

    /// <summary>
    /// One marker per point, not per rank: most points serve more than one rank, and three
    /// icons stacked on the same pixel would just look like a rendering fault. The tooltip
    /// carries which ranks it is for instead.
    /// </summary>
    private static List<(SpawnPoint Point, uint Icon, string Tooltip)> Collect(uint mapId, Configuration config)
    {
        var wanted = new List<(SpawnPoint, uint, string)>();

        foreach (var point in SpawnPoints.For(mapId))
        {
            var ranks = new List<string>();
            if (config.ShowS && point.S)
                ranks.Add("S");
            if (config.ShowA && point.A)
                ranks.Add("A");
            if (config.ShowB && point.B)
                ranks.Add("B");

            if (ranks.Count == 0)
                continue;

            // The rarest rank the point serves decides the icon, since that is the one you
            // are looking for if it is on the list at all.
            var icon = ranks[0] switch
            {
                "S" => Icons.OrFallback(config.SIcon, Configuration.DefaultSIcon),
                "A" => Icons.OrFallback(config.AIcon, Configuration.DefaultAIcon),
                _ => Icons.OrFallback(config.BIcon, Configuration.DefaultBIcon),
            };

            wanted.Add((point, icon, $"Spawn point - {string.Join(", ", ranks)} rank"));
        }

        return wanted;
    }
}
