using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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

    /// <summary>Past this the measurement is more likely to be a bug than a real sighting.</summary>
    private const float MaxSaneRange = 200f;

    public Zones.ZoneInfo? Zone { get; private set; }
    public List<CofferSpot> Spots { get; } = [];
    public bool SpotsLoaded { get; private set; }

    public int ReportedSilver { get; private set; }
    public int ReportedBronze { get; private set; }
    public DateTime? SightAt { get; private set; }
    public uint CurrentMapId { get; private set; }

    /// <summary>
    /// The shortest distance at which a coffer has ever popped into the object table this
    /// visit, and so the furthest we can claim to reliably see one. If some coffer only
    /// appeared at 40 yalms, nothing beyond 40 can be trusted to have been looked at, no
    /// matter that another one happened to show up at 120. Only ever shrinks.
    /// </summary>
    public float ObservedRange { get; private set; }

    public bool RangeMeasured => ObservedRange > 0f;

    /// <summary>How many coffers the measurement rests on.</summary>
    public int RangeSamples { get; private set; }

    /// <summary>The radius a spot has to fall inside before we claim to have checked it.</summary>
    public float EffectiveRange => config.AutoDetectionRange
        ? (RangeMeasured ? Math.Clamp(ObservedRange, config.MinDetectionRange, MaxSaneRange) : config.MinDetectionRange)
        : config.CheckRadius;

    // A coffer that was already loaded when we arrived says nothing about detection range,
    // and neither does one that appears right after a teleport - so measurements only count
    // once the zone has settled and we have not just jumped across it.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);
    private const float TeleportJump = 30f;

    private readonly HashSet<ulong> presentCoffers = [];
    private DateTime measureFrom = DateTime.MaxValue;
    private Vector3? lastSweepPosition;

    public int Reported(CofferKind kind) => kind == CofferKind.Silver ? ReportedSilver : ReportedBronze;

    public IEnumerable<CofferSpot> Of(CofferKind kind) => Spots.Where(s => s.Kind == kind);

    /// <summary>Coffers of this kind already accounted for, whether by seeing one or by
    /// being told about it. Looting one does not un-account for it.</summary>
    public int Found(CofferKind kind) => Of(kind).Count(s => s.HadCoffer);

    /// <summary>How many of the reported coffers are still unaccounted for.</summary>
    public int Outstanding(CofferKind kind) => Math.Max(0, Reported(kind) - Found(kind));

    /// <summary>Spots that could still be hiding one of the unaccounted-for coffers.</summary>
    public List<CofferSpot> Candidates(CofferKind kind)
        => Of(kind).Where(s => !s.Checked && !s.HadCoffer).ToList();

    /// <summary>
    /// Everywhere a coffer is known to be right now: the ones we have been told about or
    /// have seen, plus the whole point of the plugin - when the places left to look match
    /// the coffers left to find, every one of those places has a coffer in it.
    /// </summary>
    public List<CofferSpot> Confirmed(CofferKind kind)
    {
        var confirmed = Of(kind).Where(s => s.HoldsCoffer).ToList();

        if (SightAt == null)
            return confirmed;

        var outstanding = Outstanding(kind);
        if (outstanding <= 0)
            return confirmed;

        var candidates = Candidates(kind);
        if (candidates.Count == outstanding)
            confirmed.AddRange(candidates);

        return confirmed;
    }

    /// <summary>The elimination has narrowed things down, as opposed to us merely having
    /// been told where a coffer is.</summary>
    public bool Deduced(CofferKind kind)
    {
        if (SightAt == null)
            return false;

        var outstanding = Outstanding(kind);
        return outstanding > 0 && Candidates(kind).Count == outstanding;
    }

    public bool IsConfirmed(CofferKind kind) => Confirmed(kind).Count > 0;

    public void LeaveZone()
    {
        Zone = null;
        Spots.Clear();
        SpotsLoaded = false;
        ObservedRange = 0f;
        RangeSamples = 0;
        presentCoffers.Clear();
        lastSweepPosition = null;
        measureFrom = DateTime.UtcNow + SettleDelay;
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
            spot.HadCoffer = false;
            spot.ReportedCoffer = false;
            spot.CofferGone = false;
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

    // Eureka Linker announces a coffer it has spotted as "Treasure (Bronze): <map link>".
    private static readonly Regex TreasureReportPattern = new(
        @"^\s*Treasure\s*\(\s*(Silver|Bronze)\s*\)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How far a reported position may be from a spot and still be that spot.</summary>
    private const float ReportMatchRadius = 12f;

    /// <summary>
    /// Takes another plugin's word for it when it says a coffer is at a place. That is a
    /// stronger fact than anything we can work out ourselves, so the spot goes straight to
    /// confirmed and stops being somewhere a coffer might be hiding.
    /// </summary>
    public bool TryHandleTreasureReport(SeString message)
    {
        if (Zone == null || !SpotsLoaded)
            return false;

        var match = TreasureReportPattern.Match(message.TextValue);
        if (!match.Success)
            return false;

        var kind = match.Groups[1].Value.Equals("Silver", StringComparison.OrdinalIgnoreCase)
            ? CofferKind.Silver
            : CofferKind.Bronze;

        var link = message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (link == null)
        {
            Svc.Log.Warning("[OccultCoffers] Treasure report had no map link, so there is nowhere to put it");
            return false;
        }

        // A map link's raw position is the world position in thousandths, and its Y is the
        // world Z - so this needs no coordinate maths of our own.
        var reported = new Vector2(link.RawX / 1000f, link.RawY / 1000f);
        var mapId = link.Map.RowId;

        CofferSpot? best = null;
        var bestDistance = ReportMatchRadius * ReportMatchRadius;
        foreach (var spot in Of(kind))
        {
            if (spot.MapId != mapId)
                continue;

            var distance = Vector2.DistanceSquared(reported, new Vector2(spot.World.X, spot.World.Z));
            if (distance > bestDistance)
                continue;

            bestDistance = distance;
            best = spot;
        }

        if (best == null)
        {
            Svc.Log.Warning($"[OccultCoffers] {kind} coffer reported at {reported} on map {mapId}, " +
                            "but no known spot is near enough to be it");
            return false;
        }

        best.HadCoffer = true;
        best.ReportedCoffer = true;
        best.CofferGone = false;
        return true;
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

        var here = new Vector2(player.Position.X, player.Position.Z);
        var coffers = LiveCoffers(player.Position, here);

        var radiusSquared = EffectiveRange * EffectiveRange;

        foreach (var spot in Spots)
        {
            // Only spots on the floor we are standing on can be judged from here.
            if (spot.MapId != CurrentMapId)
                continue;

            if (Vector2.DistanceSquared(here, new Vector2(spot.World.X, spot.World.Z)) > radiusSquared)
                continue;

            spot.Checked = true;

            // Re-read occupancy every pass rather than only on the first look: a coffer that
            // was here a moment ago and is not here now is one that just got looted, and
            // that is what turns it from confirmed back into swept.
            var occupied = coffers.Any(c => c.Kind == spot.Kind
                && Vector3.DistanceSquared(c.Position, spot.World) <= SpotOccupiedRadius * SpotOccupiedRadius);

            if (occupied)
            {
                spot.HadCoffer = true;
                spot.CofferGone = false;
            }
            else
            {
                spot.CofferGone = true;
            }
        }
    }

    /// <summary>
    /// Coffers standing in the world right now. Same test BOCCHI uses - object kind Treasure,
    /// still valid and still targetable - so one that has already been looted stops counting.
    /// The moment one first appears is also the measurement the detection range rests on.
    /// </summary>
    private List<(Vector3 Position, CofferKind Kind)> LiveCoffers(Vector3 playerPosition, Vector2 here)
    {
        var coffers = new List<(Vector3, CofferKind)>();
        var treasures = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Treasure>();
        var seenNow = new HashSet<ulong>();

        var jumped = lastSweepPosition is { } previous
                     && Vector3.Distance(previous, playerPosition) > TeleportJump;
        var measuring = DateTime.UtcNow >= measureFrom && !jumped;
        lastSweepPosition = playerPosition;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Treasure)
                continue;
            if (!obj.IsValid() || obj.IsDead || !obj.IsTargetable)
                continue;

            CofferKind kind;
            if (treasures != null && treasures.TryGetRow(obj.BaseId, out var treasure))
            {
                switch (treasure.SGB.RowId)
                {
                    case 1596: kind = CofferKind.Bronze; break;
                    case 1597: kind = CofferKind.Silver; break;
                    default: continue;
                }
            }
            else
            {
                continue;
            }

            coffers.Add((obj.Position, kind));
            seenNow.Add(obj.GameObjectId);

            // Only the first frame a coffer exists says anything: that is the distance the
            // game was willing to stream it in at. Keep the shortest such distance, because
            // the range has to be one every coffer would have cleared, not the luckiest one.
            if (!measuring || presentCoffers.Contains(obj.GameObjectId))
                continue;

            var flat = Vector2.Distance(here, new Vector2(obj.Position.X, obj.Position.Z));
            RangeSamples++;
            if (!RangeMeasured || flat < ObservedRange)
                ObservedRange = flat;
        }

        presentCoffers.Clear();
        presentCoffers.UnionWith(seenNow);
        return coffers;
    }
}
