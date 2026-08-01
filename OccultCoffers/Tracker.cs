using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace OccultCoffers;

/// <summary>
/// Holds what Treasuresight said, what has been swept since, and the deduction that falls
/// out of the two.
/// </summary>
internal sealed class Tracker(Configuration config)
{
    // "You sense the presence of 2 silver coffers and 25 bronze coffers in the area!"
    private static readonly Regex SightPattern = new(
        @"sense the presence of\s+(\d+)\s+silver\s+coffers?\s+and\s+(\d+)\s+bronze\s+coffers?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The reading is only as good as the moment it was taken; coffers come and go.
    private const string NoCofferPattern = "no treasure coffers in the area";

    /// <summary>How near a coffer object has to be to a spot to count as standing on it.</summary>
    private const float SpotOccupiedRadius = 5f;

    public Zones.ZoneInfo? Zone { get; private set; }
    public List<CofferSpot> Spots { get; } = [];
    public bool SpotsLoaded { get; private set; }

    public int ReportedSilver { get; private set; }
    public int ReportedBronze { get; private set; }
    public DateTime? SightAt { get; private set; }
    public uint CurrentMapId { get; private set; }

    public int Reported(CofferKind kind) => kind == CofferKind.Silver ? ReportedSilver : ReportedBronze;

    public IEnumerable<CofferSpot> Of(CofferKind kind) => Spots.Where(s => s.Kind == kind);

    /// <summary>Coffers of this kind already accounted for by walking into them.</summary>
    public int Found(CofferKind kind) => Of(kind).Count(s => s.SawCoffer);

    /// <summary>How many of the reported coffers are still unaccounted for.</summary>
    public int Outstanding(CofferKind kind) => Math.Max(0, Reported(kind) - Found(kind));

    public List<CofferSpot> Candidates(CofferKind kind) => Of(kind).Where(s => !s.Checked).ToList();

    /// <summary>
    /// The whole point: when the places left to look match the coffers left to find, every
    /// one of those places has a coffer in it.
    /// </summary>
    public List<CofferSpot> Confirmed(CofferKind kind)
    {
        if (SightAt == null)
            return [];

        var outstanding = Outstanding(kind);
        if (outstanding <= 0)
            return [];

        var candidates = Candidates(kind);
        return candidates.Count == outstanding ? candidates : [];
    }

    public bool IsConfirmed(CofferKind kind) => Confirmed(kind).Count > 0;

    public void LeaveZone()
    {
        Zone = null;
        Spots.Clear();
        SpotsLoaded = false;
        Forget();
    }

    /// <summary>Drops the reading but keeps the spot list - used when the count is stale.</summary>
    public void Forget()
    {
        ReportedSilver = 0;
        ReportedBronze = 0;
        SightAt = null;
        foreach (var spot in Spots)
        {
            spot.Checked = false;
            spot.SawCoffer = false;
        }
    }

    /// <summary>Returns true if the line was a Treasuresight reading.</summary>
    public bool TryHandleMessage(string text)
    {
        if (Zone == null)
            return false;

        var match = SightPattern.Match(text);
        if (match.Success)
        {
            StartCycle(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            return true;
        }

        if (text.Contains(NoCofferPattern, StringComparison.OrdinalIgnoreCase))
        {
            StartCycle(0, 0);
            return true;
        }

        // Only shout about a miss when the line was clearly meant for us - a silent
        // no-op here would look exactly like the plugin working.
        if (text.Contains("sense the presence", StringComparison.OrdinalIgnoreCase)
            && text.Contains("coffer", StringComparison.OrdinalIgnoreCase))
        {
            Svc.Log.Warning($"[OccultCoffers] Could not read the Treasuresight line: \"{text}\"");
            Svc.Chat.Print("[Occult Coffers] Treasuresight said something I could not parse - the count was not updated.");
        }

        return false;
    }

    private void StartCycle(int silver, int bronze)
    {
        // A fresh reading resets the sweep: every spot is unknown again.
        Forget();
        ReportedSilver = silver;
        ReportedBronze = bronze;
        SightAt = DateTime.UtcNow;
    }

    public unsafe void Update()
    {
        var territory = Svc.ClientState.TerritoryType;
        var zone = Zones.For(territory);
        if (zone == null)
        {
            if (Zone != null)
                LeaveZone();
            return;
        }

        if (Zone?.TerritoryId != zone.TerritoryId)
        {
            LeaveZone();
            Zone = zone;
        }

        var agent = AgentMap.Instance();
        if (agent != null)
            CurrentMapId = agent->CurrentMapId;

        if (!SpotsLoaded)
        {
            // The layout streams in a little after the territory does, so this keeps
            // trying until it actually has something.
            var found = CofferSpots.Read(zone, config.SubterraneCeilingY);
            if (found.Count > 0)
            {
                Spots.Clear();
                Spots.AddRange(found);
                SpotsLoaded = true;
                Svc.Log.Information($"[OccultCoffers] {zone.Name}: {Spots.Count} coffer spots " +
                                    $"({Of(CofferKind.Silver).Count()} silver, {Of(CofferKind.Bronze).Count()} bronze)");
            }
            return;
        }

        if (SightAt == null)
            return;

        Sweep();
    }

    private void Sweep()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        var here = player.Position;
        var radiusSquared = config.CheckRadius * config.CheckRadius;

        // Coffers standing in the world right now, so a spot can be resolved as occupied
        // rather than merely visited.
        var coffers = Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.Treasure)
            .Select(o => o.Position)
            .ToList();

        foreach (var spot in Spots)
        {
            if (spot.Checked)
                continue;

            // Only spots on the floor we are standing on can be judged from here.
            if (spot.MapId != CurrentMapId)
                continue;

            if (Vector2.DistanceSquared(new Vector2(here.X, here.Z), new Vector2(spot.World.X, spot.World.Z)) > radiusSquared)
                continue;

            spot.Checked = true;
            spot.SawCoffer = coffers.Any(c =>
                Vector3.DistanceSquared(c, spot.World) <= SpotOccupiedRadius * SpotOccupiedRadius);
        }
    }
}
