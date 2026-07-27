using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AchievementSheet = Lumina.Excel.Sheets.Achievement;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace GatherTally;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/gathertally";

    // The server answers one progress request at a time; keep a polite spacing.
    private const int RequestSpacingMs = 1200;
    private const int RequestTimeoutMs = 5000;
    private const int StateLoaded = 2; // AchievementState.Loaded

    // The game's own achievement categories, in the order the achievement window lists
    // them. Matched by name so a category id shuffle in a patch doesn't break us.
    private static readonly string[] SectionNames = ["Miner", "Botanist", "Fisher"];

    private sealed record GatherAchievement(uint Id, string Section, string Name, string Description, uint SheetTarget, bool IsMeta, ushort Order);

    private readonly Configuration config;
    private List<GatherAchievement>? tracked;

    private readonly Queue<uint> requestQueue = new();
    private readonly HashSet<uint> completed = [];
    private readonly List<string> refreshingSections = [];
    private int totalQueued;
    private uint pendingId;
    private long requestSentAt;
    private long lastRequestAt;
    private long lastLoadKickAt;
    private long lastRefreshFinishedAt;
    private long lastWatchCheckAt;

    private const int WatchCheckIntervalMs = 3000;
    private string statusText = "";

    private bool windowOpen;
    private readonly RetainerRun retainerRun;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        windowOpen = config.WindowOpen;
        GatherPlanner.PreferBestSelling = config.PreferBestSelling;
        retainerRun = new RetainerRun(config);

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => ToggleWindow())
        {
            HelpMessage = "Shows your miner, botanist and fisher achievement progress."
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
        SetWindowOpen(!windowOpen);
        if (windowOpen && CurrentCache().Count == 0 && Svc.ClientState.IsLoggedIn)
            StartRefresh(SectionNames);
    }

    private void SetWindowOpen(bool open)
    {
        if (windowOpen == open)
            return;

        windowOpen = open;
        config.WindowOpen = open;
        config.Save();
    }

    #region Achievement data

    /// <summary>Every achievement in the Miner, Botanist and Fisher categories.</summary>
    private List<GatherAchievement> Tracked
    {
        get
        {
            if (tracked != null)
                return tracked;

            tracked = [];
            foreach (var achievement in Svc.Data.GetExcelSheet<AchievementSheet>())
            {
                var category = achievement.AchievementCategory.ValueNullable;
                if (category == null)
                    continue;

                var categoryName = category.Value.Name.ExtractText();
                var section = SectionNames.FirstOrDefault(s => s.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                    continue;

                var name = achievement.Name.ExtractText();
                if (name.Length == 0)
                    continue;

                // Type 1 achievements carry their target in Data; type 2 ones list the
                // achievements they require instead, so they have no counter of their own.
                var isMeta = achievement.Type == 2;
                var target = isMeta ? 0u : achievement.Data.Select(d => d.RowId).FirstOrDefault(d => d != 0);

                tracked.Add(new GatherAchievement(achievement.RowId, section, name,
                    achievement.Description.ExtractText(), target, isMeta, achievement.Order));
            }

            tracked.Sort((x, y) => x.Order.CompareTo(y.Order));
            return tracked;
        }
    }

    private IEnumerable<GatherAchievement> SectionAchievements(string section)
        => Tracked.Where(a => a.Section == section);

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

    private void StartRefresh(IReadOnlyList<string> sections)
    {
        if (requestQueue.Count > 0 || pendingId != 0 || !Svc.ClientState.IsLoggedIn || sections.Count == 0)
            return;

        refreshingSections.Clear();
        refreshingSections.AddRange(sections);

        foreach (var achievement in Tracked.Where(a => sections.Contains(a.Section)))
        {
            completed.Remove(achievement.Id);
            requestQueue.Enqueue(achievement.Id);
        }

        totalQueued = requestQueue.Count;
        pendingId = 0;
        statusText = "Fetching progress...";
    }

    private bool IsRefreshing => requestQueue.Count > 0 || pendingId != 0;

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        PumpMarketLookup();
        retainerRun.Update();
        CheckWatchedAchievement();

        if (!IsRefreshing)
        {
            MaybeAutoRefresh();
            return;
        }

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
                StoreProgress(pendingId, achievements->ProgressCurrent, achievements->ProgressMax);
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
            if (CurrentCache().TryGetValue(next, out var cached) && cached.Max > 0)
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

    private void StoreProgress(uint id, uint current, uint max)
    {
        var cache = CurrentCache();
        if (!cache.TryGetValue(id, out var entry))
            cache[id] = entry = new CachedProgress();

        // Remember the value before a change so the row can show what was just gained.
        if (entry.RetrievedAt != default && current != entry.Current)
        {
            entry.Previous = entry.Current;
            entry.HasPrevious = true;
            entry.ChangedAt = DateTime.UtcNow;
        }

        entry.Current = current;
        entry.Max = max;
        entry.RetrievedAt = DateTime.UtcNow;
    }

    private void FinishRefresh()
    {
        statusText = $"Updated {DateTime.Now:HH:mm}.";
        // Item choices depend on job level and on what the bags already hold, so let a
        // refresh re-decide rather than scanning the node sheets every frame.
        GatherPlanner.ClearCache();
        lastRefreshFinishedAt = Environment.TickCount64;
        refreshingSections.Clear();
        config.Save();
    }

    /// <summary>With auto-refresh on, re-fetch the sections that are actually expanded
    /// (a collapsed section isn't being watched) once the interval has elapsed.</summary>
    /// <summary>Once the achievement a list was built for is earned, there is nothing
    /// left to gather for, so stop GatherBuddy Reborn rather than let it grind on.</summary>
    private unsafe void CheckWatchedAchievement()
    {
        if (!config.StopWhenAchieved || config.WatchedAchievementId == 0 || !Svc.ClientState.IsLoggedIn)
            return;

        var now = Environment.TickCount64;
        if (now - lastWatchCheckAt < WatchCheckIntervalMs)
            return;
        lastWatchCheckAt = now;

        var achievements = GameAchievement.Instance();
        if (achievements == null)
            return;

        // The completion bitmask has to be loaded before IsComplete means anything, and
        // it will not be if the window was never opened this session.
        if (!achievements->IsLoaded())
        {
            if (now - lastLoadKickAt > 30_000)
            {
                lastLoadKickAt = now;
                GameMain.ExecuteCommand(1001, 0, 0, 0, 0); // RequestAllAchievement
            }

            return;
        }

        if (!achievements->IsComplete((int)config.WatchedAchievementId))
            return;

        var name = config.WatchedAchievementName;
        completed.Add(config.WatchedAchievementId);
        config.WatchedAchievementId = 0;
        config.WatchedAchievementName = "";
        config.Save();
        GatherPlanner.ClearCache();

        // A retainer trip in flight would otherwise switch gathering back on when it ends.
        retainerRun.CancelResume();
        if (GatherBuddyIpc.IsAutoGatherEnabled() == true)
            GatherBuddyIpc.SetAutoGatherEnabled(false);

        Svc.Chat.Print($"[Gather Tally] \"{name}\" is done - stopped GatherBuddy Reborn.");
    }

    /// <summary>Drives the Universalis lookups the planner asked for, and re-picks once
    /// answers arrive so a row can settle on the better-selling item.</summary>
    private void PumpMarketLookup()
    {
        if (!config.PreferBestSelling || !windowOpen)
            return;

        MarketVelocity.Pump();
        if (!MarketVelocity.Dirty)
            return;

        MarketVelocity.ClearDirty();
        GatherPlanner.ClearCache();
    }

    private void MaybeAutoRefresh()
    {
        if (!config.AutoRefresh || !windowOpen || !Svc.ClientState.IsLoggedIn)
            return;

        if (Environment.TickCount64 - lastRefreshFinishedAt < config.AutoRefreshSeconds * 1000L)
            return;

        var expanded = SectionNames.Where(IsSectionOpen).ToList();
        if (expanded.Count == 0)
            return;

        StartRefresh(expanded);
    }

    private unsafe bool IsAchievementComplete(uint id)
    {
        if (completed.Contains(id))
            return true;

        var achievements = GameAchievement.Instance();
        return achievements != null && achievements->IsLoaded() && achievements->IsComplete((int)id);
    }

    private bool IsSectionOpen(string section)
        => !config.SectionOpen.TryGetValue(section, out var open) || open;

    #endregion

    #region UI

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(660, 620), ImGuiCond.FirstUseEver);
        var open = windowOpen;
        if (ImGui.Begin("Gather Tally###GatherTally", ref open))
            DrawContents();
        ImGui.End();

        if (!open)
            SetWindowOpen(false);
    }

    private void DrawContents()
    {
        var refreshing = IsRefreshing;

        if (refreshing)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), statusText);
        else if (statusText.Length > 0)
            ImGui.TextDisabled(statusText);
        else
            ImGui.TextDisabled("No data yet - hit Refresh.");

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 50);
        if (refreshing)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Refresh"))
            StartRefresh(SectionNames);
        if (refreshing)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fetches all three sections from the server. Each section\nalso has its own refresh button for a quicker update.");

        var hideCompleted = config.HideCompleted;
        if (ImGui.Checkbox("Hide completed", ref hideCompleted))
        {
            config.HideCompleted = hideCompleted;
            config.Save();
        }

        ImGui.SameLine();
        var hideMeta = config.HideMeta;
        if (ImGui.Checkbox("Hide meta", ref hideMeta))
        {
            config.HideMeta = hideMeta;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides achievements whose requirement is obtaining other\nachievements rather than gathering anything.");

        ImGui.SameLine();
        var autoRefresh = config.AutoRefresh;
        if (ImGui.Checkbox("Auto-refresh", ref autoRefresh))
        {
            config.AutoRefresh = autoRefresh;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-fetches the expanded sections on a timer so counters move\nwhile you gather. Collapsed sections are left alone.");

        ImGui.SameLine();
        var preferBestSelling = config.PreferBestSelling;
        if (ImGui.Checkbox("Best selling", ref preferBestSelling))
        {
            config.PreferBestSelling = preferBestSelling;
            config.Save();
            GatherPlanner.PreferBestSelling = preferBestSelling;
            GatherPlanner.ClearCache();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every item that fits an achievement advances it equally, so the\n"
                + "pick goes to whichever sells fastest on your home world\n"
                + "(sales per day from Universalis) instead of whichever has the\n"
                + "most nodes. Looked up in the background as rows are shown.");

        if (config.AutoRefresh)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            var seconds = config.AutoRefreshSeconds;
            if (ImGui.SliderInt("##interval", ref seconds, 15, 300, "%d s"))
            {
                config.AutoRefreshSeconds = seconds;
                config.Save();
            }
        }

        var retainerTrips = config.RetainerRunEnabled;
        if (ImGui.Checkbox("Retainer trips", ref retainerTrips))
        {
            config.RetainerRunEnabled = retainerTrips;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("While GatherBuddy Reborn is auto-gathering, pause when this\n"
                + "character's retainers come up, travel home with Lifestream, let\n"
                + "AutoRetainer handle the bell, then carry on gathering.\n\n"
                + "Needs Lifestream and AutoRetainer. Never starts gathering on its\n"
                + "own - it only interrupts a run that is already going.");

        ImGui.SameLine();
        var stopWhenAchieved = config.StopWhenAchieved;
        if (ImGui.Checkbox("Stop when earned", ref stopWhenAchieved))
        {
            config.StopWhenAchieved = stopWhenAchieved;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turns GatherBuddy Reborn's auto-gather off once the achievement\n"
                + "its list was built for is earned, instead of grinding on for\n"
                + "nothing. The list itself is left alone.");

        if (config.RetainerRunEnabled)
        {
            var explanation = retainerRun.Explain();
            if (explanation.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(retainerRun.Running
                        ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    explanation);
            }
        }

        if (config.StopWhenAchieved && config.WatchedAchievementId != 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"watching \"{config.WatchedAchievementName}\"");
        }

        ImGui.Separator();

        var cache = CurrentCache();
        foreach (var section in SectionNames)
            DrawSection(section, cache, refreshing);
    }

    private void DrawSection(string section, Dictionary<uint, CachedProgress> cache, bool refreshing)
    {
        var all = SectionAchievements(section).ToList();
        var done = all.Count(a => IsAchievementComplete(a.Id));

        var wasOpen = IsSectionOpen(section);
        ImGui.SetNextItemOpen(wasOpen, ImGuiCond.Always);
        var isOpen = ImGui.CollapsingHeader($"{section} - {done}/{all.Count} complete###gt{section}");
        if (isOpen != wasOpen)
        {
            config.SectionOpen[section] = isOpen;
            config.Save();
        }

        if (!isOpen)
            return;

        var updated = all.Select(a => cache.TryGetValue(a.Id, out var p) ? p.RetrievedAt : default)
            .DefaultIfEmpty()
            .Max();

        if (refreshing)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Refresh {section}###gtrefresh{section}"))
            StartRefresh([section]);
        if (refreshing)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(updated == default
            ? "never fetched"
            : $"last fetched {updated.ToLocalTime():HH:mm:ss}");

        var shown = 0;
        foreach (var achievement in all)
        {
            if (config.HideCompleted && IsAchievementComplete(achievement.Id))
                continue;
            if (config.HideMeta && achievement.IsMeta)
                continue;

            DrawRow(achievement, cache);
            shown++;
        }

        if (shown == 0)
            ImGui.TextDisabled("  Nothing left to show here.");

        ImGui.Spacing();
    }

    private void DrawRow(GatherAchievement achievement, Dictionary<uint, CachedProgress> cache)
    {
        var done = IsAchievementComplete(achievement.Id);
        var hasData = cache.TryGetValue(achievement.Id, out var progress);

        // Before the first fetch the sheet still knows what the target is, so the row
        // can show the goal even with no counter behind it.
        var max = hasData && progress!.Max > 0 ? progress.Max : achievement.SheetTarget;

        DrawGatherButton(achievement, done);
        ImGui.SameLine();

        ImGui.BeginGroup();
        if (done)
        {
            ImGui.TextDisabled($"{achievement.Name} - complete");
        }
        else
        {
            ImGui.TextUnformatted(achievement.Name);
            ImGui.SameLine(340);

            if (achievement.IsMeta)
            {
                ImGui.TextDisabled("meta - needs other achievements");
            }
            else if (hasData && max > 0)
            {
                var fraction = Math.Clamp(progress!.Current / (float)max, 0f, 1f);
                ImGui.ProgressBar(fraction, new Vector2(180, 0), $"{progress.Current:N0} / {max:N0}");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"need {Math.Max(0, (long)max - progress.Current):N0}");

                // Counters that moved between the last two fetches - the point of
                // refreshing while you gather.
                if (progress.HasPrevious && progress.Current > progress.Previous)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"+{progress.Current - progress.Previous:N0}");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Gained since the fetch before last, at {progress.ChangedAt.ToLocalTime():HH:mm:ss}.");
                }
            }
            else if (max > 0)
            {
                ImGui.TextDisabled($"0 / {max:N0} - hit Refresh");
            }
            else
            {
                ImGui.TextDisabled("no data yet - hit Refresh");
            }
        }
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(achievement.Description);
    }

    /// <summary>Sends the achievement's item to GatherBuddy Reborn as a fresh, active
    /// auto-gather list. Greyed out when the achievement has no single item behind it,
    /// or when GBR isn't installed.</summary>
    private void DrawGatherButton(GatherAchievement achievement, bool done)
    {
        var plan = done ? null : GatherPlanner.For(achievement.Id, achievement.Section, achievement.Description);
        var gbrLoaded = GatherBuddyBridge.Available;
        var usable = plan != null && gbrLoaded;

        if (!usable)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Gather###gtgather{achievement.Id}") && plan != null)
            SendToGatherBuddy(achievement, plan);
        if (!usable)
            ImGui.EndDisabled();

        if (!ImGui.IsItemHovered())
            return;

        if (done)
            ImGui.SetTooltip("Already complete.");
        else if (!gbrLoaded)
            ImGui.SetTooltip("GatherBuddy Reborn isn't loaded.");
        else if (plan == null)
            ImGui.SetTooltip(GatherPlanner.Explain(achievement.Description));
        else
            ImGui.SetTooltip($"Make \"{plan.ItemName}\" x{GatherPlanner.TargetQuantity:N0} the active\n"
                + $"auto-gather list in GatherBuddy Reborn.\n\n"
                + $"Picked from {plan.Reason} ({plan.Nodes} untimed nodes, e.g. {plan.Zone}).\n"
                + (plan.SalesPerDay is { } velocity
                    ? $"Sells about {velocity:0.#}/day on your world.\n"
                    : config.PreferBestSelling ? "Sales data still loading.\n" : "")
                + (plan.Exclusive
                    ? "It comes from nowhere else, so every gather counts for this one.\n"
                    : "Careful: nothing here drops only from these nodes, so this item\n"
                      + "also comes from other levels and may credit a different tier.\n")
                + "Any other active list is switched off. This does not start\nauto-gather - flip that in GatherBuddy Reborn yourself.");
    }

    private void SendToGatherBuddy(GatherAchievement achievement, GatherPlanner.Plan plan)
    {
        var error = GatherBuddyBridge.CreateAndActivate(
            $"GT: {achievement.Name}", achievement.Description, plan.ItemId, GatherPlanner.TargetQuantity,
            out var deactivated);

        if (error != null)
        {
            Svc.Chat.PrintError($"[Gather Tally] {error}");
            return;
        }

        // This is the achievement the run is now for, so it is the one worth watching.
        config.WatchedAchievementId = achievement.Id;
        config.WatchedAchievementName = achievement.Name;
        config.Save();

        Svc.Chat.Print($"[Gather Tally] GatherBuddy Reborn is now set to gather {plan.ItemName} "
            + $"x{GatherPlanner.TargetQuantity:N0} for \"{achievement.Name}\".");
        if (!plan.Exclusive)
            Svc.Chat.PrintError($"[Gather Tally] Nothing drops only from those nodes, so {plan.ItemName} "
                + "comes from other levels too and may credit a different tier of this series.");
        if (deactivated.Count > 0)
            Svc.Chat.Print($"[Gather Tally] Switched off {deactivated.Count} other active list(s): {string.Join(", ", deactivated)}.");
    }

    #endregion
}
