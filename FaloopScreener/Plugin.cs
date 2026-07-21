using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
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
    private readonly ICallGateSubscriber<uint, string, object?> goToIpc;
    private readonly Dictionary<string, uint> zoneAetherytes = [];

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

        // CWLS Go To exposes its full Go To flow (world hop + teleport + arrival
        // follow-ups) over IPC; the per-row Go button rides on it.
        goToIpc = PluginInterface.GetIpcSubscriber<uint, string, object?>("CWLSGoTo.GoToAetheryte");

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
            // Windows are served to anonymous sessions too; a login only adds the
            // account-gated extras, so credentials are optional.
            var loginNote = "";
            if (!client.IsLoggedIn
                && !string.IsNullOrWhiteSpace(config.FaloopUsername)
                && !string.IsNullOrWhiteSpace(config.FaloopPassword))
            {
                statusText = "Logging in to Faloop...";
                if (!await client.LoginAsync(config.FaloopUsername, config.FaloopPassword).ConfigureAwait(false))
                    loginNote = " (login failed - showing public data; check Settings)";
            }

            using var doc = await client.GetDataCenterAsync("crystal").ConfigureAwait(false);
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

            // The maintenance/restart timeline lives in the app bootstrap payload.
            var restarts = new Dictionary<string, DateTime>();
            DateTime? globalRestart = null;
            using (var appDoc = await client.GetAppAsync().ConfigureAwait(false))
            {
                if (appDoc != null
                    && appDoc.RootElement.TryGetProperty("data", out var appData)
                    && appData.TryGetProperty("status", out var appStatus))
                {
                    ReadRestarts(appStatus, restarts, ref globalRestart);
                }
            }

            entries = ParseStatus(status, restarts, globalRestart);
            statusText = $"Updated {DateTime.Now:HH:mm:ss} - {entries.Count} tracked windows{loginNote}.";
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

    private static void ReadRestarts(JsonElement appStatus, Dictionary<string, DateTime> restarts, ref DateTime? globalRestart)
    {
        if (!appStatus.TryGetProperty("maintenance", out var maintenance)
            || !maintenance.TryGetProperty("restarts", out var restartsObj)
            || !restartsObj.TryGetProperty("timeline", out var timeline)
            || timeline.ValueKind != JsonValueKind.Array)
            return;

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

    /// <summary>Builds window entries from the data-center status payload:
    /// status.windows is an array of { mobId2, worldId2, startedAt (the timer's base,
    /// i.e. the last kill), startedAtOffset? (ms), num (zone instance) } covering all
    /// ranks; status.spawns lists currently reported spawns.</summary>
    private List<WindowEntry> ParseStatus(JsonElement status, Dictionary<string, DateTime> restarts, DateTime? globalRestart)
    {
        var kills = new Dictionary<(string Mob, string World), DateTime>();
        var spawns = new Dictionary<(string Mob, string World), DateTime>();

        if (status.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in windows.EnumerateArray())
                CollectArrayEntry(w, kills);
        }

        if (status.TryGetProperty("spawns", out var spawnArr) && spawnArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in spawnArr.EnumerateArray())
                CollectArrayEntry(s, spawns);
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

            // A server restart newer than the kill resets the timer and shortens the window.
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

    /// <summary>Reads one status array entry ({ mobId2, worldId2, startedAt, ... }) into
    /// the map, keeping only known S/SS marks on the configured data center's worlds.</summary>
    private static void CollectArrayEntry(JsonElement element, Dictionary<(string, string), DateTime> into)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("mobId2", out var mobEl) || mobEl.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("worldId2", out var worldEl) || worldEl.ValueKind != JsonValueKind.String)
            return;

        var mobId = mobEl.GetString()!;
        var world = worldEl.GetString()!;
        if (!FaloopData.MobsById.ContainsKey(mobId) || !FaloopData.CrystalWorlds.Contains(world))
            return;

        var ts = ReadTimestamp(element.TryGetProperty("startedAt", out var started) ? started : default)
                 ?? ReadTimestamp(element.TryGetProperty("timestamp", out var t) ? t : default);
        if (ts == null)
            return;

        if (element.TryGetProperty("startedAtOffset", out var offset) && offset.ValueKind == JsonValueKind.Number)
            ts = ts.Value.AddMilliseconds(offset.GetDouble());

        var key = (mobId, world);
        if (!into.TryGetValue(key, out var existing) || ts > existing)
            into[key] = ts.Value;
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
            .Where(e => e.Mob.Rank == "S")
            .Where(e => config.EnabledWorlds.Contains(e.WorldSlug))
            .Where(e => !config.HiddenMobs.Contains(e.Mob.Id))
            .Where(e => !e.Mob.Zones.All(z => config.HiddenZones.Contains(z)))
            .ToList();

        if (!ImGui.BeginTable("##windows", 8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Hunt", ImGuiTableColumnFlags.WidthFixed, 165);
        ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Open", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Window", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cap", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 135);
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
            DrawConditionBadge(e.Mob.Id, now);

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
            if (ImGui.SmallButton($"Go##{id}"))
                GoToZone(e);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Teleport to {FaloopData.Pretty(e.ZoneSlug)} on {FaloopData.Pretty(e.WorldSlug)}\n(via the CWLS Go To plugin).");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Spawn##{id}"))
                ImGui.OpenPopup($"spawn##{id}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Hide##{id}"))
            {
                config.HiddenMobs.Add(e.Mob.Id);
                config.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Hides {e.Mob.Name} on every world.\nUndo under \"Hidden hunts & zones\".");

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
            client.ResetAuth();
            lastFetchAt = DateTime.MinValue;
            StartFetch();
        }

        ImGui.TextDisabled("Optional - the windows are public. Same account as the Faloop\nwebsite / Faloop Integration; stored in the plugin config on this PC.");
    }

    /// <summary>Mirrors faloop.app's condition column: green "For x" while the spawn
    /// condition holds, muted "In x" until it next comes round.</summary>
    private static void DrawConditionBadge(string mobId, DateTime now)
    {
        var window = FaloopConditions.GetWindow(mobId, now);
        if (window == null)
            return;

        if (window.IsActive(now))
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"For {Short(window.End - now)}");
        else
            ImGui.TextDisabled($"In {Short(window.Start - now)}");

        if (!ImGui.IsItemHovered())
            return;

        var trigger = FaloopData.SpawnTriggers.TryGetValue(mobId, out var t) ? t : "";
        ImGui.SetTooltip(window.IsActive(now)
            ? $"Spawn condition is met for another {Short(window.End - now)}.\n{trigger}"
            : $"Spawn condition returns in {Short(window.Start - now)}\nand then lasts {Short(window.End - window.Start)}.\n{trigger}");
    }

    private void GoToZone(WindowEntry e)
    {
        var aetheryteId = ResolveZoneAetheryte(e.ZoneSlug);
        if (aetheryteId == 0)
        {
            Svc.Chat.Print($"[FaloopScreener] No aetheryte found for {FaloopData.Pretty(e.ZoneSlug)}.");
            return;
        }

        try
        {
            goToIpc.InvokeAction(aetheryteId, FaloopData.Pretty(e.WorldSlug));
        }
        catch (Exception ex)
        {
            Svc.Chat.Print("[FaloopScreener] Could not start Go To - is the CWLS Go To plugin up to date?");
            Svc.Log.Warning($"Go To IPC failed: {ex.Message}");
        }
    }

    /// <summary>First aetheryte in the zone matching the Faloop zone slug, compared with
    /// apostrophes stripped ("yak_tel" vs "Yak T'el", "kozamauka" vs "Kozama'uka").</summary>
    private uint ResolveZoneAetheryte(string zoneSlug)
    {
        if (zoneAetherytes.TryGetValue(zoneSlug, out var cached))
            return cached;

        var target = NormalizeZoneName(zoneSlug.Replace('_', ' '));
        uint found = 0;
        foreach (var aetheryte in Svc.Data.GetExcelSheet<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte)
                continue;

            var place = aetheryte.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText();
            if (place != null && NormalizeZoneName(place) == target)
            {
                found = aetheryte.RowId;
                break;
            }
        }

        zoneAetherytes[zoneSlug] = found;
        return found;
    }

    private static string NormalizeZoneName(string name)
        => new(name.ToLowerInvariant().Where(c => c != '\'' && c != '’' && c != '-').ToArray());

    /// <summary>Compact duration in the style Faloop uses for the badges: 3d / 10h / 20m.</summary>
    private static string Short(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero)
            ts = TimeSpan.Zero;
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private static string Hm(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero)
            ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}";
    }

    #endregion
}
