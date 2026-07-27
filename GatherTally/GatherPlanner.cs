using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GatherTally;

/// <summary>Works out something you can actually gather to advance a given achievement.
/// Only the "gather N times" achievements have an answer: they name a node level range
/// (and sometimes a region), which maps onto the gathering point sheets. Achievements
/// about big fish, ocean fishing scores, skyward points or unique discoveries have no
/// single item behind them and are left alone.</summary>
public static class GatherPlanner
{
    public sealed record Plan(uint ItemId, string ItemName, string Zone, int Nodes, string Reason, bool Exclusive);

    private sealed class Candidate(uint itemId, string name, int perception, byte level, string zone)
    {
        public uint ItemId { get; } = itemId;
        public string Name { get; } = name;
        public int Perception { get; } = perception;
        public byte Level { get; } = level;
        public string Zone { get; set; } = zone;
        public int Nodes { get; set; }

        /// <summary>True when no node outside the wanted band yields this item.</summary>
        public bool Exclusive { get; set; }

        /// <summary>True when the node sits in the expansion whose levels these are.</summary>
        public bool RightExpansion { get; set; }
    }

    // GatheringType: 0 Mining, 1 Quarrying (both Miner), 2 Logging, 3 Harvesting (both
    // Botanist). The ARR achievements say "mineral deposits" / "mature trees" but count
    // the whole class - some level brackets have no node of the narrower type at all.
    private static readonly uint[] MinerTypes = [0, 1];
    private static readonly uint[] BotanistTypes = [2, 3];

    private static readonly Regex LevelRange = new(@"level (\d+)-(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The sheet's "this node has no ephemeral window" sentinel.
    private const ushort NoEphemeralTime = 65535;

    // Every ordinary gathering zone is an open-world map (TerritoryIntendedUse 1). The
    // one exception holding nodes is the Diadem, which is instanced content an
    // auto-gatherer cannot simply walk into.
    private const uint OpenWorldIntendedUse = 1;

    // Quantity the auto-gather list is set to, and therefore the point at which an item
    // would already be finished before we start.
    public const uint TargetQuantity = 9999;

    private static readonly Dictionary<uint, Plan?> cache = [];

    // One flattened row per (node, item it yields). Built once - a per-achievement scan
    // of the point sheets would stall the frame with fifty rows on screen.
    private sealed record NodeEntry(uint GatheringType, byte Level, string Region, string Zone, uint ItemId, string ItemName, int Perception, uint Expansion);

    private static List<NodeEntry>? gatheringIndex;
    private static List<NodeEntry>? fishingIndex;

    public static void ClearCache() => cache.Clear();

    /// <summary>The item to gather for this achievement, or null when the achievement
    /// isn't a "gather N times" grind. Cached per achievement.</summary>
    public static Plan? For(uint achievementId, string section, string description)
    {
        if (cache.TryGetValue(achievementId, out var cached))
            return cached;

        var plan = Build(section, description);
        cache[achievementId] = plan;
        return plan;
    }

    /// <summary>Why the button is greyed out, for the tooltip.</summary>
    public static string Explain(string description)
    {
        if (LevelRange.IsMatch(description) || description.Contains("Gatherer's Boon", StringComparison.OrdinalIgnoreCase))
            return "No untimed node matches this achievement.";

        return "This one isn't a plain \"gather N times\" achievement, so there is no\nsingle item that advances it.";
    }

    private static Plan? Build(string section, string description)
    {
        int lo, hi;
        string reason;

        var match = LevelRange.Match(description);
        if (match.Success)
        {
            lo = int.Parse(match.Groups[1].Value);
            hi = int.Parse(match.Groups[2].Value);
            reason = $"level {lo}-{hi} nodes";
        }
        else if (description.Contains("Gatherer's Boon", StringComparison.OrdinalIgnoreCase))
        {
            // Any node counts, so gather at the top of what this job can reach.
            lo = 1;
            hi = Math.Max(1, JobLevel(section));
            reason = $"any node up to level {hi}";
        }
        else
        {
            return null;
        }

        string? region = null;
        if (description.Contains("in La Noscea", StringComparison.OrdinalIgnoreCase))
            region = "La Noscea";
        else if (description.Contains("in the Black Shroud", StringComparison.OrdinalIgnoreCase))
            region = "The Black Shroud";
        else if (description.Contains("in Thanalan", StringComparison.OrdinalIgnoreCase))
            region = "Thanalan";

        if (region != null)
            reason += $" in {region}";

        var scoped = section switch
        {
            "Miner" => GatheringIndex.Where(e => MinerTypes.Contains(e.GatheringType)),
            "Botanist" => GatheringIndex.Where(e => BotanistTypes.Contains(e.GatheringType)),
            _ => FishingIndex.AsEnumerable(),
        };

        var candidates = Collect(scoped, lo, hi, region, ExpansionForLevel(hi));

        // Preference order, strongest first. "Right expansion" comes before exclusivity
        // because a level 80 node sitting in a Heavensward zone is an Ishgardian
        // Restoration node - exclusive, but not where these levels are meant to be
        // gathered. Exclusivity then keeps out anything that also drops outside the
        // band: shards and crystals above all, which let the auto-gatherer pick a node
        // that credits a different tier, and is how "La Noscea III" fed "La Noscea V".
        return Choose(candidates.Where(c => c.RightExpansion && c.Exclusive).ToList(), reason)
            ?? Choose(candidates.Where(c => c.Exclusive).ToList(), reason)
            ?? Choose(candidates.Where(c => c.RightExpansion).ToList(), reason)
            ?? Choose(candidates, reason);
    }

    /// <summary>The expansion whose own zones hold nodes of this level.</summary>
    private static uint ExpansionForLevel(int level) => level switch
    {
        <= 50 => 0,
        <= 60 => 1,
        <= 70 => 2,
        <= 80 => 3,
        <= 90 => 4,
        _ => 5,
    };

    private static Plan? Choose(List<Candidate> candidates, string reason)
    {
        // Most nodes first, and never something the bags are already full of - the
        // auto-gather list would count as finished before it started.
        foreach (var candidate in candidates
                     .OrderBy(c => c.Perception)
                     .ThenByDescending(c => c.Nodes)
                     .ThenByDescending(c => c.Level))
        {
            if (HeldCount(candidate.ItemId) >= TargetQuantity)
                continue;

            return new Plan(candidate.ItemId, candidate.Name, candidate.Zone, candidate.Nodes, reason, candidate.Exclusive);
        }

        return null;
    }

    private static List<Candidate> Collect(IEnumerable<NodeEntry> source, int lo, int hi, string? region, uint expansion)
    {
        var found = new Dictionary<uint, Candidate>();
        var strays = new HashSet<uint>();

        foreach (var entry in source)
        {
            var inBand = entry.Level >= lo && entry.Level <= hi
                && (region == null || entry.Region.Equals(region, StringComparison.OrdinalIgnoreCase));

            if (!inBand)
            {
                strays.Add(entry.ItemId);
                continue;
            }

            if (!found.TryGetValue(entry.ItemId, out var candidate))
                found[entry.ItemId] = candidate = new Candidate(entry.ItemId, entry.ItemName, entry.Perception, entry.Level, entry.Zone);
            candidate.Nodes++;

            // One matching node is enough to make the item worth gathering there; record
            // that zone as the example rather than whichever came first.
            if (entry.Expansion == expansion && !candidate.RightExpansion)
            {
                candidate.RightExpansion = true;
                candidate.Zone = entry.Zone;
            }
        }

        foreach (var candidate in found.Values)
            candidate.Exclusive = !strays.Contains(candidate.ItemId);

        return found.Values.ToList();
    }

    private static List<NodeEntry> GatheringIndex => gatheringIndex ??= BuildGatheringIndex();

    private static List<NodeEntry> BuildGatheringIndex()
    {
        var index = new List<NodeEntry>();
        var gatheringItems = Svc.Data.GetExcelSheet<GatheringItem>();
        var transients = Svc.Data.GetExcelSheet<GatheringPointTransient>();
        var items = Svc.Data.GetExcelSheet<Item>();

        foreach (var point in Svc.Data.GetExcelSheet<GatheringPoint>())
        {
            if (point.TerritoryType.RowId == 0)
                continue;

            var baseRow = point.GatheringPointBase.ValueNullable;
            var territory = point.TerritoryType.ValueNullable;
            if (baseRow == null || territory == null)
                continue;
            if (territory.Value.TerritoryIntendedUse.RowId != OpenWorldIntendedUse)
                continue;

            var zone = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (zone.Length == 0)
                continue;
            var pointRegion = territory.Value.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? "";

            // Unspoiled and ephemeral nodes only exist for a few Eorzean hours, which is
            // no use for a grind of thousands.
            var transient = transients.GetRowOrDefault(point.RowId);
            if (transient != null
                && (transient.Value.GatheringRarePopTimeTable.RowId != 0
                    || (transient.Value.EphemeralStartTime != 0 && transient.Value.EphemeralStartTime != NoEphemeralTime)))
                continue;

            foreach (var itemRef in baseRow.Value.Item)
            {
                var gatheringItem = gatheringItems.GetRowOrDefault((uint)itemRef.RowId);
                if (gatheringItem == null || gatheringItem.Value.IsHidden || gatheringItem.Value.Item.RowId == 0)
                    continue;

                var item = items.GetRowOrDefault(gatheringItem.Value.Item.RowId);
                var name = item?.Name.ExtractText() ?? "";
                if (name.Length == 0)
                    continue;

                index.Add(new NodeEntry(baseRow.Value.GatheringType.RowId, baseRow.Value.GatheringLevel,
                    pointRegion, zone, gatheringItem.Value.Item.RowId, name, gatheringItem.Value.PerceptionReq,
                    territory.Value.ExVersion.RowId));
            }
        }

        return index;
    }

    private static List<NodeEntry> FishingIndex => fishingIndex ??= BuildFishingIndex();

    private static List<NodeEntry> BuildFishingIndex()
    {
        var index = new List<NodeEntry>();
        var items = Svc.Data.GetExcelSheet<Item>();

        foreach (var spot in Svc.Data.GetExcelSheet<FishingSpot>())
        {
            if (spot.Rare || spot.TerritoryType.RowId == 0)
                continue;

            var territory = spot.TerritoryType.ValueNullable;
            if (territory == null || territory.Value.TerritoryIntendedUse.RowId != OpenWorldIntendedUse)
                continue;

            var zone = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (zone.Length == 0)
                continue;
            var spotRegion = territory.Value.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? "";

            foreach (var itemRef in spot.Item)
            {
                if (itemRef.RowId == 0)
                    continue;

                var item = items.GetRowOrDefault((uint)itemRef.RowId);
                var name = item?.Name.ExtractText() ?? "";
                if (name.Length == 0)
                    continue;

                index.Add(new NodeEntry(0, spot.GatheringLevel, spotRegion, zone, (uint)itemRef.RowId, name, 0,
                    territory.Value.ExVersion.RowId));
            }
        }

        return index;
    }

    /// <summary>The player's level in the section's own class, not whatever they happen
    /// to have equipped.</summary>
    private static unsafe int JobLevel(string section)
    {
        var classJobId = section switch { "Miner" => 16u, "Botanist" => 17u, _ => 18u };
        var expIndex = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.ExpArrayIndex ?? -1;
        if (expIndex < 0)
            return 100;

        var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (state == null || expIndex >= state->ClassJobLevels.Length)
            return 100;

        var level = state->ClassJobLevels[expIndex];
        return level > 0 ? level : 100;
    }

    private static unsafe uint HeldCount(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : (uint)Math.Max(0, manager->GetInventoryItemCount(itemId));
    }
}
