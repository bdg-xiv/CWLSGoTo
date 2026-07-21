using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;

namespace FaloopScreener;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/windows";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private enum WindowState { Up, Open, Capped, Closed }

    private sealed record WindowEntry(
        FaloopData.MobInfo Mob, string WorldSlug, string ZoneSlug,
        WindowState State, DateTime? WindowStart, DateTime? CapAt, double Percent);

    private readonly Configuration config;
    private readonly FaloopClient client = new();

    private bool windowOpen;
    private volatile List<WindowEntry> entries = [];
    private volatile string statusText = "Not connected yet.";
    private DateTime lastFetchAt = DateTime.MinValue;
    private bool fetching;
    private bool dumpNextPayload;
    private string zoneToHide = "";

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Faloop spawn windows table. \"/windows debug\" logs the raw tracker payload."
        });

        PluginInterface.UiBuilder.Draw += DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= DrawWindow;
        Svc.Commands.RemoveHandler(CommandName);
        client.Dispose();
        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => windowOpen = !windowOpen;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            dumpNextPayload = true;
            windowOpen = true;
            Svc.Chat.Print("[FaloopScreener] The next refresh will log the raw tracker payload (/xllog).");
            return;
        }

        windowOpen = !windowOpen;
    }

    #region Data fetching

    private void StartFetch()
    {
        if (fetching)
            return;

        fetching = true;
        lastFetchAt = DateTime.UtcNow;
        _ = Task.Run(FetchAsync);
    }

    private async Task FetchAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.FaloopUsername) || string.IsNullOrWhiteSpace(config.FaloopPassword))
            {
                statusText = "Set your Faloop account under Settings below - windows are only served to logged-in accounts.";
                return;
            }

            if (!client.IsLoggedIn)
            {
                statusText = "Logging in to Faloop...";
                if (!await client.LoginAsync(config.FaloopUsername, config.FaloopPassword).ConfigureAwait(false))
                {
                    statusText = "Faloop login failed - check the username/password in Settings.";
                    return;
                }
            }

            using var doc = await client.GetAppAsync().ConfigureAwait(false);
            if (doc == null)
            {
                statusText = "Could not fetch the tracker state from Faloop.";
                return;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("status", out var status))
            {
                statusText = "Unexpected Faloop payload (no status).";
                return;
            }

            var rawStatus = status.GetRawText();
            Svc.Log.Debug($"Faloop status payload ({rawStatus.Length} chars): {rawStatus[..Math.Min(rawStatus.Length, 4000)]}");
            if (dumpNextPayload)
            {
                dumpNextPayload = false;
                foreach (var chunk in Chunks(rawStatus, 6000).Take(10))
                    Svc.Log.Information($"[FaloopScreener debug] {chunk}");
            }

            entries = ParseStatus(status);
            statusText = entries.Count == 0
                ? "Connected, but no window data was found - your Faloop account may lack tracker access, or the payload shape changed (run \"/windows debug\" and check /xllog)."
                : $"Updated {DateTime.Now:HH:mm:ss} - {entries.Count} tracked windows.";
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Faloop fetch failed");
            statusText = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            fetching = false;
        }
    }

    private static IEnumerable<string> Chunks(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    /// <summary>Builds window entries from Faloop's status payload. The exact shape of a
    /// logged-in payload isn't documented, so this scans every dictionary in the status
    /// for composite keys naming a known mob + world and pulls kill/spawn timestamps out
    /// of the values wherever they are.</summary>
    private List<WindowEntry> ParseStatus(JsonElement status)
    {
        var kills = new Dictionary<(string Mob, string World), DateTime>();
        var spawns = new Dictionary<(string Mob, string World), DateTime>();

        foreach (var section in status.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object || section.Name is "maintenance")
                continue;

            foreach (var entry in section.Value.EnumerateObject())
            {
                var mob = FaloopData.Mobs
                    .Where(m => entry.Name.Contains(m.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.Id.Length)
                    .FirstOrDefault();
                if (mob == null)
                    continue;

                var world = FaloopData.CrystalWorlds.FirstOrDefault(w => entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (world == null)
                    continue;

                CollectTimestamps(entry.Value, 0, (mob.Id, world), kills, spawns);
            }
        }

        // Server restarts reset the timers and shorten the windows; a restart newer than
        // the kill replaces it as the countdown base.
        var restarts = new Dictionary<string, DateTime>();
        DateTime? globalRestart = null;
        if (status.TryGetProperty("maintenance", out var maintenance)
            && maintenance.TryGetProperty("restarts", out var restartsObj)
            && restartsObj.TryGetProperty("timeline", out var timeline)
            && timeline.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in timeline.EnumerateArray())
            {
                var ts = ReadTimestamp(r.TryGetProperty("timestamp", out var t) ? t : default);
                if (ts == null)
                    continue;
                if (r.TryGetProperty("worldId", out var w) && w.ValueKind == JsonValueKind.String)
                {
                    var slug = w.GetString()!;
                    if (!restarts.TryGetValue(slug, out var existing) || ts > existing)
                        restarts[slug] = ts.Value;
                }
                else if (globalRestart == null || ts > globalRestart)
                {
                    globalRestart = ts;
                }
            }
        }

        var now = DateTime.UtcNow;
        var result = new List<WindowEntry>();
        foreach (var key in kills.Keys.Concat(spawns.Keys).Distinct())
        {
            var mob = FaloopData.MobsById[key.Mob];
            kills.TryGetValue(key, out var killedAt);
            var hasKill = kills.ContainsKey(key);
            var isUp = spawns.TryGetValue(key, out var spawnedAt) && (!hasKill || spawnedAt > killedAt);

            if (isUp)
            {
                result.Add(new WindowEntry(mob, key.World, mob.Zones[0], WindowState.Up, null, null, 1));
                continue;
            }

            if (!hasKill)
                continue;

            var baseTime = killedAt;
            var (min, cap) = (mob.Min, mob.Cap);
            var restart = restarts.TryGetValue(key.World, out var wr) ? wr : (DateTime?)null;
            if (globalRestart != null && (restart == null || globalRestart > restart))
                restart = globalRestart;
            if (restart != null && restart > killedAt)
            {
                baseTime = restart.Value;
                (min, cap) = (mob.MaintMin, mob.MaintCap);
            }

            var windowStart = baseTime.AddHours(min);
            var capAt = baseTime.AddHours(cap);
            var state = now < windowStart ? WindowState.Closed : now < capAt ? WindowState.Open : WindowState.Capped;
            var percent = state switch
            {
                WindowState.Closed => 0,
                WindowState.Capped => 1,
                _ => capAt > windowStart ? (now - windowStart).TotalSeconds / (capAt - windowStart).TotalSeconds : 1,
            };

            result.Add(new WindowEntry(mob, key.World, mob.Zones[0], state, windowStart, capAt, percent));
        }

        return result
            .OrderByDescending(e => e.State == WindowState.Up)
            .ThenByDescending(e => e.Percent)
            .ThenBy(e => e.WindowStart ?? DateTime.MaxValue)
            .ToList();
    }

    private static void CollectTimestamps(JsonElement element, int depth,
        (string, string) key,
        Dictionary<(string, string), DateTime> kills,
        Dictionary<(string, string), DateTime> spawns)
    {
        if (depth > 4 || element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                CollectTimestamps(prop.Value, depth + 1, key, kills, spawns);
                continue;
            }

            var ts = ReadTimestamp(prop.Value);
            if (ts == null)
                continue;

            if (prop.Name is "killedAt" or "diedAt" or "deathAt" or "timestamp")
            {
                if (!kills.TryGetValue(key, out var existing) || ts > existing)
                    kills[key] = ts.Value;
            }
            else if (prop.Name is "spawnedAt" or "startedAt")
            {
                if (!spawns.TryGetValue(key, out var existing) || ts > existing)
                    spawns[key] = ts.Value;
            }
        }
    }

    private static DateTime? ReadTimestamp(JsonElement value)
    {
        try
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                        ? dt
                        : null;
                case JsonValueKind.Number:
                    var n = value.GetDouble();
                    // Epoch milliseconds (anything after ~2003 in ms).
                    return n > 1_000_000_000_000 ? DateTime.UnixEpoch.AddMilliseconds(n) : null;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region UI

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        if (DateTime.UtcNow - lastFetchAt > RefreshInterval)
            StartFetch();

        ImGui.SetNextWindowSize(new Vector2(760, 480), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Faloop Spawn Windows###FaloopScreener", ref windowOpen))
        {
            ImGui.TextWrapped(statusText);
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh"))
            {
                lastFetchAt = DateTime.MinValue;
                StartFetch();
            }

            DrawWorldFilter();
            DrawTable();
            DrawFilterSection();
            DrawSettingsSection();
        }

        ImGui.End();
    }

    private void DrawWorldFilter()
    {
        var first = true;
        foreach (var world in FaloopData.CrystalWorlds)
        {
            if (!first)
                ImGui.SameLine();
            first = false;

            var enabled = config.EnabledWorlds.Contains(world);
            if (ImGui.Checkbox(FaloopData.Pretty(world), ref enabled))
            {
                if (enabled)
                    config.EnabledWorlds.Add(world);
                else
                    config.EnabledWorlds.Remove(world);
                config.Save();
            }
        }
    }

    private void DrawTable()
    {
        var visible = entries
            .Where(e => config.EnabledWorlds.Contains(e.WorldSlug))
            .Where(e => !config.HiddenMobs.Contains(e.Mob.Id))
            .Where(e => !e.Mob.Zones.All(z => config.HiddenZones.Contains(z)))
            .ToList();

        if (!ImGui.BeginTable("##windows", 7,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Hunt", ImGuiTableColumnFlags.WidthFixed, 165);
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Open", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;
        foreach (var e in visible)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FaloopData.Pretty(e.WorldSlug));
            ImGui.TableNextColumn();
            if (e.State == WindowState.Up)
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), e.Mob.Name);
            else
                ImGui.TextUnformatted(e.Mob.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FaloopData.Pretty(e.ZoneSlug));

            ImGui.TableNextColumn();
            switch (e.State)
            {
                case WindowState.Up:
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "UP!");
                    break;
                case WindowState.Closed:
                    ImGui.TextDisabled($"in {Hm(e.WindowStart!.Value - now)}");
                    break;
                default:
                    ImGui.TextUnformatted($"For {Hm(now - e.WindowStart!.Value)}");
                    break;
            }

            ImGui.TableNextColumn();
            if (e.State == WindowState.Up)
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "spawned - go get it!");
            else
                ImGui.ProgressBar((float)e.Percent, new Vector2(-1, 0), $"{e.Percent * 100:N0}%");

            ImGui.TableNextColumn();
            if (e.CapAt != null)
            {
                if (now < e.CapAt.Value)
                    ImGui.TextUnformatted($"in {Hm(e.CapAt.Value - now)}");
                else
                    ImGui.TextDisabled($"{Hm(now - e.CapAt.Value)} ago");
            }

            ImGui.TableNextColumn();
            var id = $"{e.Mob.Id}@{e.WorldSlug}";
            if (ImGui.SmallButton($"Spawn##{id}"))
                ImGui.OpenPopup($"spawn##{id}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Hide##{id}"))
            {
                config.HiddenMobs.Add(e.Mob.Id);
                config.Save();
            }

            if (ImGui.BeginPopup($"spawn##{id}"))
            {
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"{e.Mob.Name} - spawn trigger");
                ImGui.Separator();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420);
                ImGui.TextWrapped(FaloopData.SpawnTriggers.TryGetValue(e.Mob.Id, out var trigger)
                    ? trigger
                    : "No trigger information available.");
                ImGui.PopTextWrapPos();
                ImGui.EndPopup();
            }
        }

        ImGui.EndTable();

        if (visible.Count == 0)
            ImGui.TextDisabled("No windows to show (check filters, or wait for the first refresh).");
    }

    private void DrawFilterSection()
    {
        if (!ImGui.CollapsingHeader("Hidden hunts & zones"))
            return;

        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##zonepick", zoneToHide.Length > 0 ? FaloopData.Pretty(zoneToHide) : "Select zone to hide..."))
        {
            foreach (var zone in FaloopData.Mobs.SelectMany(m => m.Zones).Distinct().OrderBy(z => z))
            {
                if (ImGui.Selectable(FaloopData.Pretty(zone), zone == zoneToHide))
                    zoneToHide = zone;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Hide zone") && zoneToHide.Length > 0)
        {
            config.HiddenZones.Add(zoneToHide);
            config.Save();
            zoneToHide = "";
        }

        foreach (var zone in config.HiddenZones.ToList())
        {
            if (ImGui.SmallButton($"Unhide##z{zone}"))
            {
                config.HiddenZones.Remove(zone);
                config.Save();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"Zone: {FaloopData.Pretty(zone)}");
        }

        foreach (var mobId in config.HiddenMobs.ToList())
        {
            if (ImGui.SmallButton($"Unhide##m{mobId}"))
            {
                config.HiddenMobs.Remove(mobId);
                config.Save();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"Hunt: {(FaloopData.MobsById.TryGetValue(mobId, out var m) ? m.Name : mobId)}");
        }

        if (config.HiddenMobs.Count == 0 && config.HiddenZones.Count == 0)
            ImGui.TextDisabled("Nothing hidden. Use the Hide button on a row or pick a zone above.");
    }

    private void DrawSettingsSection()
    {
        if (!ImGui.CollapsingHeader("Settings (Faloop account)"))
            return;

        var username = config.FaloopUsername;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Username", ref username, 128))
            config.FaloopUsername = username;

        var password = config.FaloopPassword;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Password", ref password, 128, ImGuiInputTextFlags.Password))
            config.FaloopPassword = password;

        if (ImGui.Button("Save & Login"))
        {
            config.Save();
            lastFetchAt = DateTime.MinValue;
            StartFetch();
        }

        ImGui.TextDisabled("Same account as the Faloop website / Faloop Integration.\nStored in the plugin config on this PC.");
    }

    private static string Hm(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero)
            ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}";
    }

    #endregion
}
