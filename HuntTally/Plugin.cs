using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
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

    private sealed record HuntAchievement(uint Id, string Name, string Description, string Rank, string Expansion, ushort Order);

    private readonly Configuration config;
    private List<HuntAchievement>? tracked;

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
                tracked.Add(new HuntAchievement(achievement.RowId, name, description,
                    ClassifyRank(description), ClassifyExpansion(description), achievement.Order));
            }

            return tracked;
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
            statusText = $"Updated {DateTime.Now:HH:mm}.";
            config.Save();
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
            return;
        }

        achievements->RequestAchievementProgress(next);
        pendingId = next;
        requestSentAt = now;
        lastRequestAt = now;
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

            ImGui.Separator();

            var cache = CurrentCache();
            foreach (var expansion in ExpansionOrder)
            {
                var group = Tracked.Where(a => a.Expansion == expansion).ToList();
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

        var label = achievement.Rank.Length > 0 ? $"[{achievement.Rank[..^1]}] " : "";
        if (done)
            ImGui.TextDisabled($"{label}{achievement.Name} - complete");
        else if (hasData && progress!.Max > 0)
        {
            ImGui.TextUnformatted($"{label}{achievement.Name}");
            ImGui.SameLine(280);
            var fraction = Math.Clamp(progress.Current / (float)progress.Max, 0f, 1f);
            ImGui.ProgressBar(fraction, new Vector2(180, 0), $"{progress.Current:N0} / {progress.Max:N0}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"need {progress.Max - progress.Current:N0}");
        }
        else
        {
            ImGui.TextUnformatted($"{label}{achievement.Name}");
            ImGui.SameLine(280);
            ImGui.TextDisabled("no data yet - hit Refresh");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(achievement.Description);
    }

    #endregion
}
