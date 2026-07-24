using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace HuntTally;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/hunttally";

    // The server answers one progress request at a time; keep a polite spacing.
    private const int RequestSpacingMs = 1200;
    private const int RequestTimeoutMs = 5000;
    private const int StateLoaded = 2; // AchievementState.Loaded

    private static readonly string[] ExpansionOrder =
        ["A Realm Reborn", "Heavensward", "Stormblood", "Shadowbringers", "Endwalker", "Dawntrail", "Overall totals"];

    private sealed record HuntAchievement(uint Id, string Name, string Description, string Rank, string Expansion, ushort Order)
    {
        // Meta achievements ("Complete all ... elite mark achievements") ask for other
        // achievements instead of kills; their descriptions are the ones that mention
        // the word "achievement".
        public bool RequiresOtherAchievements { get; } = Description.Contains("achievement", StringComparison.OrdinalIgnoreCase);

        // Set for "slay each unique mark" achievements (the "Mark of the ..." series).
        public List<UniqueMark>? UniqueMarks { get; set; }

        // For meta achievements (sheet Type 2): the achievements they require, and
        // whether the reward item is a mount (ItemAction type 1322).
        public List<uint>? MetaRequirements { get; set; }
        public bool AwardsMount { get; set; }
    }

    private sealed record UniqueMark(uint NotoriousId, uint BNpcNameId, string Name, string Zone);

    // The "Mark of the ..." achievements ask for every elite mark of one rank in a zone
    // group. Their criteria ids in the Achievement sheet are server-side only, but the
    // member marks are derivable: NotoriousMonsterTerritory has one row per zone (in
    // release order) listing that zone's marks, so each achievement is a fixed set of
    // those rows filtered by rank (B=1, A=2, S=3).
    private static readonly (uint AchievementId, byte Rank, uint[] Zones)[] UniqueKillAchievements =
    [
        (966, 1, [1, 2, 3, 4, 5, 17]), (971, 2, [1, 2, 3, 4, 5, 17]), (976, 3, [1, 2, 3, 4, 5, 17]), // La Noscea
        (965, 1, [6, 7, 8, 9, 10]), (970, 2, [6, 7, 8, 9, 10]), (975, 3, [6, 7, 8, 9, 10]),          // Thanalan
        (964, 1, [11, 12, 13, 14]), (969, 2, [11, 12, 13, 14]), (974, 3, [11, 12, 13, 14]),          // Black Shroud
        (967, 1, [15, 16]), (972, 2, [15, 16]), (977, 3, [15, 16]),                                  // Coerthas/Mor Dhona
        (1260, 1, [18, 22, 23]), (1262, 2, [18, 22, 23]), (1264, 3, [18, 22, 23]),                   // Cloud and Ice
        (1259, 1, [19, 20, 21]), (1261, 2, [19, 20, 21]), (1263, 3, [19, 20, 21]),                   // Dravania
        (1910, 1, [24, 27, 28]), (1911, 2, [24, 27, 28]), (1912, 3, [24, 27, 28]),                   // Gyr Abania
        (1913, 1, [25, 26, 29]), (1914, 2, [25, 26, 29]), (1915, 3, [25, 26, 29]),                   // Othard
    ];

    private static readonly Dictionary<uint, string> ZoneNames = new()
    {
        [1] = "Middle La Noscea", [2] = "Lower La Noscea", [3] = "Eastern La Noscea",
        [4] = "Western La Noscea", [5] = "Upper La Noscea", [17] = "Outer La Noscea",
        [6] = "Western Thanalan", [7] = "Central Thanalan", [8] = "Eastern Thanalan",
        [9] = "Southern Thanalan", [10] = "Northern Thanalan",
        [11] = "Central Shroud", [12] = "East Shroud", [13] = "South Shroud", [14] = "North Shroud",
        [15] = "Coerthas Central Highlands", [16] = "Mor Dhona",
        [18] = "Coerthas Western Highlands", [22] = "The Sea of Clouds", [23] = "Azys Lla",
        [19] = "The Dravanian Forelands", [20] = "The Dravanian Hinterlands", [21] = "The Churning Mists",
        [24] = "The Fringes", [27] = "The Peaks", [28] = "The Lochs",
        [25] = "The Ruby Sea", [26] = "Yanxia", [29] = "The Azim Steppe",
    };

    private readonly Configuration config;
    private List<HuntAchievement>? tracked;
    private readonly Dictionary<uint, uint> markWatch = [];   // BNpcName id -> NotoriousMonster id
    private readonly Dictionary<uint, string> markNames = []; // NotoriousMonster id -> display name
    private long lastMarkScanAt;

    private bool windowOpen;
    private readonly Queue<uint> requestQueue = new();
    private readonly HashSet<uint> completed = [];
    private int totalQueued;
    private uint pendingId;
    private long requestSentAt;
    private long lastRequestAt;
    private long lastLoadKickAt;
    private string statusText = "";

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "Shows your hunt achievement progress per expansion."
        });

        Svc.Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= DrawWindow;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private void ToggleWindow()
    {
        windowOpen = !windowOpen;
        if (windowOpen && CurrentCache().Count == 0 && Svc.ClientState.IsLoggedIn)
            StartRefresh();
    }

    #region Achievement data

    /// <summary>All achievements in the game's "The Hunt" category, classified by rank
    /// and expansion from their English descriptions.</summary>
    private List<HuntAchievement> Tracked
    {
        get
        {
            if (tracked != null)
                return tracked;

            tracked = [];
            foreach (var achievement in Svc.Data.GetExcelSheet<AchievementSheet>())
            {
                var category = achievement.AchievementCategory.ValueNullable;
                if (category == null || !category.Value.Name.ExtractText().Equals("The Hunt", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = achievement.Name.ExtractText();
                if (name.Length == 0)
                    continue;

                var description = achievement.Description.ExtractText();
                var entry = new HuntAchievement(achievement.RowId, name, description,
                    ClassifyRank(description), ClassifyExpansion(description), achievement.Order);

                if (achievement.Type == 2)
                    entry.MetaRequirements = achievement.Data.Where(d => d.RowId != 0).Select(d => d.RowId).ToList();
                entry.AwardsMount = achievement.Item.RowId != 0
                    && achievement.Item.ValueNullable?.ItemAction.ValueNullable?.Action.RowId == 1322;

                tracked.Add(entry);
            }

            ResolveUniqueMarks(tracked);
            return tracked;
        }
    }

    private void ResolveUniqueMarks(List<HuntAchievement> achievements)
    {
        var byId = achievements.ToDictionary(a => a.Id);
        var zoneSheet = Svc.Data.GetExcelSheet<NotoriousMonsterTerritory>();
        markWatch.Clear();
        markNames.Clear();

        foreach (var (achievementId, rank, zones) in UniqueKillAchievements)
        {
            if (!byId.TryGetValue(achievementId, out var achievement))
                continue;

            var marks = new List<UniqueMark>();
            foreach (var zoneRow in zones)
            {
                var row = zoneSheet.GetRowOrDefault(zoneRow);
                if (row == null)
                    continue;

                var zone = ZoneNames.GetValueOrDefault(zoneRow, "?");
                foreach (var markRef in row.Value.NotoriousMonsters)
                {
                    var monster = markRef.ValueNullable;
                    if (markRef.RowId == 0 || monster == null || monster.Value.Rank != rank)
                        continue;

                    var name = monster.Value.BNpcName.ValueNullable?.Singular.ExtractText() ?? "";
                    if (name.Length == 0)
                        continue;

                    name = char.ToUpperInvariant(name[0]) + name[1..];
                    marks.Add(new UniqueMark(markRef.RowId, monster.Value.BNpcName.RowId, name, zone));
                    markWatch[monster.Value.BNpcName.RowId] = markRef.RowId;
                    markNames[markRef.RowId] = name;
                }
            }

            achievement.UniqueMarks = marks;
        }
    }

    private static string ClassifyRank(string description)
    {
        if (description.Contains("SS rank", StringComparison.OrdinalIgnoreCase))
            return "SS ranks";
        if (description.Contains("S rank", StringComparison.OrdinalIgnoreCase))
            return "S ranks";
        if (description.Contains("A rank", StringComparison.OrdinalIgnoreCase))
            return "A ranks";
        if (description.Contains("B rank", StringComparison.OrdinalIgnoreCase))
            return "B ranks";
        return "";
    }

    private static string ClassifyExpansion(string description)
    {
        foreach (var expansion in (string[])["Heavensward", "Stormblood", "Shadowbringers", "Endwalker", "Dawntrail"])
        {
            if (description.Contains(expansion, StringComparison.OrdinalIgnoreCase))
                return expansion;
        }

        // The Shadowbringers counters name the place, not the expansion.
        if (description.Contains("Norvrandt", StringComparison.OrdinalIgnoreCase))
            return "Shadowbringers";

        // Rank achievements without an expansion callout are the ARR ones; anything
        // else (plain elite-mark kill totals) goes into the overall bucket.
        return ClassifyRank(description).Length > 0 ? "A Realm Reborn" : "Overall totals";
    }

    #endregion

    #region Progress fetching

    private Dictionary<uint, CachedProgress> CurrentCache()
    {
        var character = Svc.PlayerState.ContentId;
        if (character == 0)
            return [];

        if (!config.ProgressByCharacter.TryGetValue(character, out var cache))
        {
            cache = [];
            config.ProgressByCharacter[character] = cache;
        }

        return cache;
    }

    private HashSet<uint> CurrentSlain()
    {
        var character = Svc.PlayerState.ContentId;
        if (character == 0)
            return [];

        if (!config.SlainMarksByCharacter.TryGetValue(character, out var slain))
        {
            slain = [];
            config.SlainMarksByCharacter[character] = slain;
        }

        return slain;
    }

    // The server never says which unique marks are done, only how many, so record a
    // mark as slain when its corpse is seen near the player (hunt credit is shared,
    // so being on top of a dying mark practically always counts).
    private void ScanForMarkDeaths()
    {
        var now = Environment.TickCount64;
        if (now - lastMarkScanAt < 1000)
            return;
        lastMarkScanAt = now;

        if (!Svc.ClientState.IsLoggedIn || Player.Object is not { } playerObject)
            return;

        _ = Tracked; // builds the mark watch list on first use
        if (markWatch.Count == 0)
            return;

        var playerPosition = playerObject.Position;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || !npc.IsDead)
                continue;
            if (!markWatch.TryGetValue(npc.NameId, out var notoriousId))
                continue;
            if (Vector3.DistanceSquared(playerPosition, npc.Position) > 60f * 60f)
                continue;

            if (CurrentSlain().Add(notoriousId))
            {
                config.Save();
                Svc.Chat.Print($"[HuntTally] Recorded unique elite mark kill: {markNames[notoriousId]}.");
            }
        }
    }

    private void StartRefresh()
    {
        if (requestQueue.Count > 0 || !Svc.ClientState.IsLoggedIn)
            return;

        completed.Clear();
        foreach (var achievement in Tracked)
            requestQueue.Enqueue(achievement.Id);
        totalQueued = requestQueue.Count;
        pendingId = 0;
        statusText = "Fetching progress...";
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        ScanForMarkDeaths();

        if (requestQueue.Count == 0 && pendingId == 0)
            return;

        if (!Svc.ClientState.IsLoggedIn)
        {
            requestQueue.Clear();
            pendingId = 0;
            return;
        }

        var achievements = GameAchievement.Instance();
        if (achievements == null)
            return;

        var now = Environment.TickCount64;

        // The completion bitmask loads when the achievement window is opened; kick the
        // same request ourselves and wait for it.
        if (!achievements->IsLoaded())
        {
            if (now - lastLoadKickAt > 5000)
            {
                lastLoadKickAt = now;
                GameMain.ExecuteCommand(1001, 0, 0, 0, 0); // RequestAllAchievement
            }

            statusText = "Loading achievement data from the server...";
            return;
        }

        if (pendingId != 0)
        {
            if (achievements->ProgressAchievementId == pendingId && (int)achievements->ProgressRequestState == StateLoaded)
            {
                CurrentCache()[pendingId] = new CachedProgress
                {
                    Current = achievements->ProgressCurrent,
                    Max = achievements->ProgressMax,
                    RetrievedAt = DateTime.UtcNow,
                };
                pendingId = 0;
            }
            else if (now - requestSentAt > RequestTimeoutMs)
            {
                Svc.Log.Warning($"Achievement progress request {pendingId} timed out");
                pendingId = 0;
            }
            else
            {
                return;
            }
        }

        if (requestQueue.Count == 0)
        {
            FinishRefresh();
            return;
        }

        statusText = $"Fetching progress... {totalQueued - requestQueue.Count + 1}/{totalQueued}";

        if (now - lastRequestAt < RequestSpacingMs)
            return;

        var next = requestQueue.Dequeue();
        if (achievements->IsComplete((int)next))
        {
            completed.Add(next);
            // A completed achievement's counter no longer matters; make sure the cache
            // shows it full if we ever fetched partial numbers before.
            if (CurrentCache().TryGetValue(next, out var cached))
                cached.Current = cached.Max;
            if (requestQueue.Count == 0)
                FinishRefresh();
            return;
        }

        achievements->RequestAchievementProgress(next);
        pendingId = next;
        requestSentAt = now;
        lastRequestAt = now;
    }

    private void FinishRefresh()
    {
        statusText = $"Updated {DateTime.Now:HH:mm}.";
        RecordTallySnapshot();
        config.Save();
    }

    // The counters behind the estimates: the per-expansion "III" tiers and the
    // overall Bring Your A/S Game series counters.
    private const uint ShbAThree = 2352;
    private const uint EwAThree = 2996;
    private const uint DtAThree = 3533;
    private const uint OverallA = 1918;
    private const uint ShbSThree = 2355;
    private const uint EwSThree = 2999;
    private const uint DtSThree = 3536;
    private const uint OverallS = 1921;
    private const uint BringSGameFive = 1920; // the overall S tier the mount meta needs

    // Tiers of one series share a counter; use a sibling's recorded pace when the
    // achievement itself has no snapshots yet.
    private static readonly Dictionary<uint, uint> PaceAlias = new() { [BringSGameFive] = OverallS };

    private void RecordTallySnapshot()
    {
        var character = Svc.PlayerState.ContentId;
        if (character == 0)
            return;

        var cache = CurrentCache();
        var counters = new Dictionary<uint, uint>();
        foreach (var id in (uint[])[ShbAThree, EwAThree, DtAThree, OverallA, ShbSThree, EwSThree, DtSThree, OverallS, BringSGameFive])
        {
            if (cache.TryGetValue(id, out var progress) && progress.Max > 0)
                counters[id] = progress.Current;
        }

        if (counters.Count == 0)
            return;

        if (!config.TallyHistory.TryGetValue(character, out var history))
            config.TallyHistory[character] = history = [];

        var today = DateTime.UtcNow.Date;
        var entry = history.FirstOrDefault(h => h.Date == today);
        if (entry == null)
            history.Add(entry = new DailyTally { Date = today });
        entry.Counters = counters;
        history.RemoveAll(h => h.Date < today.AddDays(-60));
    }

    /// <summary>Kills per day for one counter, measured across the recorded daily
    /// snapshots (preferring the last two weeks).</summary>
    private double? PacePerDay(uint achievementId)
    {
        var character = Svc.PlayerState.ContentId;
        if (character == 0 || !config.TallyHistory.TryGetValue(character, out var history))
            return null;

        var points = history
            .Where(h => h.Counters.ContainsKey(achievementId))
            .OrderBy(h => h.Date)
            .ToList();

        var recent = points.Where(p => p.Date >= DateTime.UtcNow.Date.AddDays(-14)).ToList();
        if (recent.Count >= 2)
            points = recent;
        if (points.Count < 2)
            return PaceAlias.TryGetValue(achievementId, out var alias) ? PacePerDay(alias) : null;

        var days = (points[^1].Date - points[0].Date).TotalDays;
        if (days <= 0)
            return null;

        var gained = (double)points[^1].Counters[achievementId] - points[0].Counters[achievementId];
        return gained > 0 ? gained / days : null;
    }

    private unsafe bool IsAchievementComplete(uint id)
    {
        if (completed.Contains(id))
            return true;

        var achievements = GameAchievement.Instance();
        return achievements != null && achievements->IsLoaded() && achievements->IsComplete((int)id);
    }

    #endregion

    #region UI

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hunt Tally###HuntTally", ref windowOpen))
        {
            var fetching = requestQueue.Count > 0 || pendingId != 0;
            if (fetching)
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), statusText);
            else if (statusText.Length > 0)
                ImGui.TextDisabled(statusText);

            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60);
            if (fetching)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton("Refresh"))
                StartRefresh();
            if (fetching)
                ImGui.EndDisabled();

            var hideCompleted = config.HideCompleted;
            if (ImGui.Checkbox("Hide completed", ref hideCompleted))
            {
                config.HideCompleted = hideCompleted;
                config.Save();
            }

            ImGui.SameLine();
            var hideMeta = config.HideMetaAchievements;
            if (ImGui.Checkbox("Hide meta achievements", ref hideMeta))
            {
                config.HideMetaAchievements = hideMeta;
                config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hides achievements whose requirement is completing\nother achievements rather than killing marks.");

            ImGui.Separator();

            var cache = CurrentCache();
            if (ImGui.CollapsingHeader("Counters & pace###htCounters", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawTrainEstimate(cache);
                DrawSEstimate(cache);
                DrawMountSEstimate(cache);
            }

            foreach (var expansion in ExpansionOrder)
            {
                var group = Tracked.Where(a => a.Expansion == expansion
                        && (!config.HideCompleted || !IsAchievementComplete(a.Id))
                        && (!config.HideMetaAchievements || !a.RequiresOtherAchievements))
                    .ToList();
                if (group.Count == 0)
                    continue;

                if (!ImGui.CollapsingHeader($"{expansion}###ht{expansion}", ImGuiTreeNodeFlags.DefaultOpen))
                    continue;

                foreach (var rankGroup in group.GroupBy(a => a.Rank).OrderBy(g => RankOrder(g.Key)))
                {
                    foreach (var achievement in rankGroup.OrderBy(a => cache.TryGetValue(a.Id, out var c) ? c.Max : a.Order))
                        DrawRow(achievement, cache);
                }
            }
        }

        ImGui.End();
    }

    /// <summary>Rank letter as the counter descriptions actually write it: "Slay 300
    /// rank S elite marks", "elite marks of rank S or higher". SS first, since
    /// "rank SS" contains "rank S".</summary>
    private static string RankLetter(string description)
    {
        if (description.Contains("rank SS", StringComparison.OrdinalIgnoreCase))
            return "SS";
        if (description.Contains("rank S", StringComparison.OrdinalIgnoreCase))
            return "S";
        if (description.Contains("rank A", StringComparison.OrdinalIgnoreCase))
            return "A";
        if (description.Contains("rank B", StringComparison.OrdinalIgnoreCase))
            return "B";
        return "";
    }

    /// <summary>For an overall kill counter, how many kills each expansion still owes
    /// if its own counted achievements get finished. Tiers within an expansion share a
    /// counter, so an expansion's remainder is the largest gap, not the sum.</summary>
    private List<(string Expansion, long Remaining)>? ExpansionRemainders(HuntAchievement achievement, Dictionary<uint, CachedProgress> cache)
    {
        if (achievement.Expansion != "Overall totals" || achievement.UniqueMarks != null || achievement.RequiresOtherAchievements)
            return null;

        var rank = RankLetter(achievement.Description);
        if (rank.Length == 0)
            return null;

        var result = new List<(string Expansion, long Remaining)>();
        foreach (var group in Tracked
                     .Where(a => a.Expansion != "Overall totals" && a.UniqueMarks == null && !a.RequiresOtherAchievements
                         && RankLetter(a.Description) == rank && !IsAchievementComplete(a.Id))
                     .GroupBy(a => a.Expansion))
        {
            long remaining = 0;
            foreach (var entry in group)
            {
                if (cache.TryGetValue(entry.Id, out var progress) && progress.Max > 0)
                    remaining = Math.Max(remaining, progress.Max - progress.Current);
            }

            if (remaining > 0)
                result.Add((group.Key, remaining));
        }

        result.Sort((x, y) => Array.IndexOf(ExpansionOrder, x.Expansion).CompareTo(Array.IndexOf(ExpansionOrder, y.Expansion)));
        return result;
    }

    /// <summary>How many full triple trains (ShB + EW + DT, both As in every zone:
    /// 12 kills per leg, 36 total) are left for the A-rank achievements, plus an ETA
    /// from the recorded daily kill pace.</summary>
    private void DrawTrainEstimate(Dictionary<uint, CachedProgress> cache)
    {
        var targets = new List<(uint Id, string Label, int PerTrain, long Remaining)>();
        foreach (var (id, label, perTrain) in (ReadOnlySpan<(uint, string, int)>)
                 [(ShbAThree, "ShB", 12), (EwAThree, "EW", 12), (DtAThree, "DT", 12), (OverallA, "overall", 36)])
        {
            if (IsAchievementComplete(id) || !cache.TryGetValue(id, out var progress)
                || progress.Max == 0 || progress.Current >= progress.Max)
                continue;
            targets.Add((id, label, perTrain, progress.Max - progress.Current));
        }

        if (targets.Count == 0)
            return;

        var trains = targets.Max(t => (t.Remaining + t.PerTrain - 1) / t.PerTrain);
        var breakdown = string.Join(", ", targets.Select(t => $"{t.Label} {t.Remaining:N0}"));
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
            $"A-rank trains: about {trains} full triples left ({breakdown} kills to go).");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Assumes full triple trains: both A ranks in every zone,\nso 12 kills per expansion leg and 36 in total per triple.\nThe ETA below uses your recorded pace instead.");

        DrawPaceLine(targets.Select(t => (t.Id, t.Label, t.Remaining)).ToList(), OverallA, "A");
    }

    /// <summary>The S-rank counterpart: no train math (there are no S trains), just
    /// the kill remainders and the recorded-pace ETA.</summary>
    private void DrawSEstimate(Dictionary<uint, CachedProgress> cache)
    {
        var targets = new List<(uint Id, string Label, long Remaining)>();
        var parts = new List<string>();
        long expansionSum = 0;
        long? overallRemaining = null;
        foreach (var (id, label) in (ReadOnlySpan<(uint, string)>)
                 [(ShbSThree, "ShB"), (EwSThree, "EW"), (DtSThree, "DT"), (OverallS, "overall")])
        {
            if (IsAchievementComplete(id) || !cache.TryGetValue(id, out var progress)
                || progress.Max == 0 || progress.Current >= progress.Max)
                continue;
            var remaining = (long)progress.Max - progress.Current;
            targets.Add((id, label, remaining));
            if (id == OverallS)
                overallRemaining = remaining;
            else
            {
                expansionSum += remaining;
                parts.Add($"{label} {remaining:N0}");
            }
        }

        if (targets.Count == 0)
            return;

        // The overall counter is fed by the expansion grinds too; the parenthesis is
        // what must come from anywhere on top of finishing those.
        if (overallRemaining is { } overall)
            parts.Add($"overall {overall:N0} ({Math.Max(0, overall - expansionSum):N0} from anywhere)");

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"S ranks: {string.Join(", ", parts)} kills to go.");
        if (ImGui.IsItemHovered() && overallRemaining != null)
            ImGui.SetTooltip("Every S kill feeds the overall counter, so finishing the\nper-expansion achievements covers part of it; the number in\nparentheses is what still has to come from any expansion on top.");
        DrawPaceLine(targets, OverallS, "S");
    }

    /// <summary>S kills that actually gate a mount: the S-rank requirements of the
    /// meta achievements whose reward item is a mount (resolved from the sheet, so
    /// it includes Bring Your S Game V's 2,000 rather than VI's 5,000).</summary>
    private void DrawMountSEstimate(Dictionary<uint, CachedProgress> cache)
    {
        var targets = new List<(uint Id, string Label, long Remaining)>();
        var parts = new List<string>();
        var byId = Tracked.ToDictionary(a => a.Id);

        foreach (var meta in Tracked.Where(a => a.AwardsMount && a.MetaRequirements != null && !IsAchievementComplete(a.Id)))
        {
            foreach (var requirementId in meta.MetaRequirements!)
            {
                if (!byId.TryGetValue(requirementId, out var requirement)
                    || RankLetter(requirement.Description) != "S"
                    || IsAchievementComplete(requirementId)
                    || targets.Any(t => t.Id == requirementId))
                    continue;

                if (!cache.TryGetValue(requirementId, out var progress) || progress.Max == 0 || progress.Current >= progress.Max)
                    continue;

                targets.Add((requirementId, ShortExpansion(requirement.Expansion), (long)progress.Max - progress.Current));
                parts.Add($"{ShortExpansion(requirement.Expansion)} {progress.Max - progress.Current:N0}");
            }
        }

        if (targets.Count == 0)
            return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"S ranks for mounts: {string.Join(", ", parts)} kills to go.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the S-rank requirements that gate a mount reward\n(Centurio Tiger, Triceratops, Victor, Ullr metas). The overall\ntier here is Bring Your S Game V at 2,000 - VI has no mount.");
        DrawPaceLine(targets, OverallS, "S");
    }

    private static string ShortExpansion(string expansion) => expansion switch
    {
        "A Realm Reborn" => "ARR",
        "Heavensward" => "HW",
        "Stormblood" => "StB",
        "Shadowbringers" => "ShB",
        "Endwalker" => "EW",
        "Dawntrail" => "DT",
        _ => "overall",
    };

    private void DrawPaceLine(List<(uint Id, string Label, long Remaining)> targets, uint overallId, string rank)
    {
        double? worstDays = null;
        var worstLabel = "";
        foreach (var (id, label, remaining) in targets)
        {
            var pace = PacePerDay(id);
            if (pace == null)
                continue;
            var days = remaining / pace.Value;
            if (worstDays == null || days > worstDays)
            {
                worstDays = days;
                worstLabel = label;
            }
        }

        if (worstDays == null)
        {
            ImGui.TextDisabled($"Pace: recording your daily {rank}-rank kills - an ETA appears once refreshes exist on two different days.");
            return;
        }

        var overallPace = PacePerDay(overallId);
        var paceNote = overallPace != null ? $" ({overallPace:N0} {rank} kills/day)" : "";
        var finish = DateTime.Now.AddDays(worstDays.Value);
        ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
            $"At your recent pace{paceNote}: about {Math.Ceiling(worstDays.Value):N0} days left (slowest: {worstLabel}) - finishing around {finish:d MMM yyyy}.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The counters progress in parallel (each only advances with kills in\nits own expansion), so the finish date is set by the slowest one -\nlines sharing the same slowest requirement share the same date.");
    }

    private static int RankOrder(string rank) => rank switch
    {
        "S ranks" => 0,
        "SS ranks" => 1,
        "A ranks" => 2,
        "B ranks" => 3,
        _ => 4,
    };

    private void DrawRow(HuntAchievement achievement, Dictionary<uint, CachedProgress> cache)
    {
        var done = IsAchievementComplete(achievement.Id);
        var hasData = cache.TryGetValue(achievement.Id, out var progress);
        var remainders = done ? null : ExpansionRemainders(achievement, cache);
        var projectedExtra = remainders?.Sum(r => r.Remaining) ?? 0;

        ImGui.BeginGroup();
        var label = achievement.Rank.Length > 0 ? $"[{achievement.Rank[..^1]}] " : "";
        if (done)
            ImGui.TextDisabled($"{label}{achievement.Name} - complete");
        else if (hasData && progress!.Max > 0)
        {
            ImGui.TextUnformatted($"{label}{achievement.Name}");
            ImGui.SameLine(280);
            var fraction = Math.Clamp(progress.Current / (float)progress.Max, 0f, 1f);
            var barSize = new Vector2(180, 0);
            if (projectedExtra > 0)
            {
                // Ghost fill behind the real one: where the counter lands once the
                // per-expansion achievements are ground out.
                var projected = Math.Clamp((progress.Current + projectedExtra) / (float)progress.Max, 0f, 1f);
                var cursor = ImGui.GetCursorPos();
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.35f, 0.55f, 0.35f, 0.55f));
                ImGui.ProgressBar(projected, barSize, "");
                ImGui.PopStyleColor();
                ImGui.SetCursorPos(cursor);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
                ImGui.ProgressBar(fraction, barSize, $"{progress.Current:N0} / {progress.Max:N0}");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.ProgressBar(fraction, barSize, $"{progress.Current:N0} / {progress.Max:N0}");
            }

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"need {progress.Max - progress.Current:N0}");
        }
        else
        {
            ImGui.TextUnformatted($"{label}{achievement.Name}");
            ImGui.SameLine(280);
            ImGui.TextDisabled("no data yet - hit Refresh");
        }
        ImGui.EndGroup();

        var marks = achievement.UniqueMarks;
        var popupId = $"##huntmarks{achievement.Id}";
        if (marks != null && !done && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(popupId);

        if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen(popupId))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(achievement.Description);
            if (remainders is { Count: > 0 } && hasData && progress!.Max > 0)
            {
                var projected = progress.Current + projectedExtra;
                ImGui.Separator();
                ImGui.TextUnformatted($"After finishing the per-expansion achievements: {Math.Min(projected, progress.Max):N0} / {progress.Max:N0}");
                foreach (var (expansion, remaining) in remainders)
                    ImGui.TextDisabled($"  {expansion}: +{remaining:N0}");
                if (projected >= progress.Max)
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f),
                        $"Those grinds alone finish this, with {projected - progress.Max:N0} kills to spare.");
                else
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                        $"Still {progress.Max - projected:N0} kills short after those - any expansion counts.");
            }

            if (marks != null)
            {
                var slain = CurrentSlain();
                ImGui.Separator();
                foreach (var mark in marks)
                {
                    if (done || slain.Contains(mark.NotoriousId))
                        ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"slain - {mark.Name} ({mark.Zone})");
                    else
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"needed - {mark.Name} ({mark.Zone})");
                }

                if (!done)
                {
                    ImGui.Separator();
                    var recorded = marks.Count(m => slain.Contains(m.NotoriousId));
                    if (hasData && progress!.Max > 0 && recorded != progress.Current)
                        ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f),
                            $"Plugin has {recorded} recorded but the server counts {progress.Current}.");
                    ImGui.TextDisabled("The game only reports the total, so kills are recorded here\nwhen a mark dies near you. Right-click to fix older kills.");
                }
            }

            ImGui.EndTooltip();
        }

        if (marks != null && !done && ImGui.BeginPopup(popupId))
        {
            var slain = CurrentSlain();
            ImGui.TextDisabled($"{achievement.Name} - tick the marks you have already slain");
            ImGui.Separator();
            foreach (var mark in marks)
            {
                var isSlain = slain.Contains(mark.NotoriousId);
                if (ImGui.Checkbox($"{mark.Name} ({mark.Zone})###mk{achievement.Id}-{mark.NotoriousId}", ref isSlain))
                {
                    if (isSlain)
                        slain.Add(mark.NotoriousId);
                    else
                        slain.Remove(mark.NotoriousId);
                    config.Save();
                }
            }

            ImGui.EndPopup();
        }
    }

    #endregion
}
