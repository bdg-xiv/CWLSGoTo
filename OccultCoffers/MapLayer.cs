using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KamiToolKit.MapOverlay;

namespace OccultCoffers;

/// <summary>
/// Draws the spots as its own node layer on the map. This is deliberately not the game's
/// gathering-marker slots: those are shared, capped at a dozen, and are exactly where
/// another plugin's chest marks would already be sitting.
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

    public void Sync(Tracker tracker, Configuration config)
    {
        if (!config.Enabled || tracker.Zone == null || !tracker.SpotsLoaded)
        {
            Clear();
            return;
        }

        var wanted = Collect(tracker, config);

        // Rebuilding nodes every frame would be silly; only redo it when the picture
        // actually changed.
        var signature = Signature(wanted);
        if (signature == drawn)
            return;

        controller.RemoveAllMarkers();
        markers.Clear();

        foreach (var (spot, icon, size, tooltip) in wanted)
        {
            var marker = new MapMarkerNode
            {
                IconId = icon,
                MapId = spot.MapId,
                Size = new Vector2(size, size),
                Position = new Vector2(spot.World.X, spot.World.Z),
                TextTooltip = tooltip,
            };
            markers.Add(marker);
            controller.AddMarker(marker);
        }

        drawn = signature;
    }

    private static List<(CofferSpot Spot, uint Icon, float Size, string Tooltip)> Collect(Tracker tracker, Configuration config)
    {
        var wanted = new List<(CofferSpot, uint, float, string)>();

        foreach (var kind in new[] { CofferKind.Silver, CofferKind.Bronze })
        {
            var confirmed = tracker.Confirmed(kind);
            var confirmedKeys = new HashSet<ulong>();
            foreach (var spot in confirmed)
                confirmedKeys.Add(spot.Key);

            if (config.ShowConfirmed)
            {
                var icon = kind == CofferKind.Silver ? config.SilverIcon : config.BronzeIcon;
                foreach (var spot in confirmed)
                    wanted.Add((spot, icon, config.ConfirmedIconSize, $"{kind} coffer - confirmed by elimination"));
            }

            foreach (var spot in tracker.Of(kind))
            {
                if (confirmedKeys.Contains(spot.Key))
                    continue;

                if (!spot.Checked)
                {
                    if (config.ShowCandidates)
                        wanted.Add((spot, config.CandidateIcon, config.CandidateIconSize, $"{kind} spot - not swept yet"));
                }
                else if (config.ShowCleared)
                {
                    wanted.Add((spot, config.ClearedIcon, config.CandidateIconSize,
                        spot.SawCoffer ? $"{kind} spot - coffer found here" : $"{kind} spot - swept, empty"));
                }
            }
        }

        return wanted;
    }

    private static string Signature(List<(CofferSpot Spot, uint Icon, float Size, string Tooltip)> wanted)
    {
        var builder = new StringBuilder();
        foreach (var (spot, icon, size, _) in wanted)
            builder.Append(spot.Key).Append(':').Append(icon).Append(':').Append(size).Append('|');
        return builder.ToString();
    }
}
