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

namespace HookNamer;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/hooknamer";

    private static readonly Vector4 Good = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    private bool windowOpen;
    private string loadError = "";
    private string status = "";
    private List<AutoHookPresets.PresetInfo> presets = [];

    /// <summary>Chosen target fish per preset, keyed by preset name.</summary>
    private readonly Dictionary<string, uint> chosen = [];

    /// <summary>Search text inside each preset's fish picker popup.</summary>
    private readonly Dictionary<string, string> searches = [];

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Hook Namer - makes AutoHook presets usable by GatherBuddy Reborn."
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= Toggle;
        PluginInterface.UiBuilder.Draw -= Draw;
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private void Toggle()
    {
        windowOpen = !windowOpen;
        if (windowOpen)
            Reload();
    }

    private void OnCommand(string command, string args) => Toggle();

    private void Reload()
    {
        presets = AutoHookPresets.Load(out loadError);
        chosen.Clear();
        searches.Clear();

        // Community presets name the fish in parentheses; pre-fill whatever we recognise.
        foreach (var preset in presets.Where(p => !p.IsIdNamed))
        {
            var guess = FishIndex.Guess(preset.Name);
            if (guess != 0)
                chosen[preset.Name] = guess;
        }
    }

    #region UI

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(780, 540), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hook Namer###HookNamer", ref windowOpen))
        {
            DrawHeader();
            DrawTable();
            DrawFooter();
        }

        ImGui.End();
    }

    private void DrawHeader()
    {
        ImGui.TextWrapped("GatherBuddy Reborn only reuses an AutoHook preset when the preset's name is exactly "
                          + "the target fish's item id - \"7714 Glimmerscale\" will not match, it has to be \"7714\". "
                          + "Pick each preset's target fish and Hook Namer adds an id-named copy; your originals are left alone.");
        ImGui.TextDisabled("The target fish isn't stored inside a preset, so it's guessed from the preset's name - check it before creating.");
        ImGui.Separator();

        if (ImGui.SmallButton("Reload"))
            Reload();

        ImGui.SameLine();
        if (!AutoHookPresets.AutoHookAvailable())
            ImGui.TextColored(Bad, "AutoHook is not responding - is it installed and enabled?");
        else if (loadError.Length > 0)
            ImGui.TextColored(Bad, loadError);
        else
            ImGui.TextDisabled($"{presets.Count} presets in AutoHook.");

        if (status.Length > 0)
            ImGui.TextColored(Good, status);
    }

    private void DrawTable()
    {
        var existingIds = presets.Where(p => p.IsIdNamed).Select(p => p.Name).ToHashSet();

        if (!ImGui.BeginTable("##presets", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
                new Vector2(0, 380)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Target fish", ImGuiTableColumnFlags.WidthFixed, 250);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        foreach (var preset in presets)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (preset.IsIdNamed)
                ImGui.TextColored(Good, preset.Name);
            else
                ImGui.TextUnformatted(preset.Name.Length > 0 ? preset.Name : "(unnamed)");

            if (ImGui.IsItemHovered() && (preset.FishIds.Length > 0 || preset.BaitIds.Length > 0))
            {
                ImGui.SetTooltip($"Hook rules inside this preset (not the target):\n"
                                 + $"fish: {Describe(preset.FishIds)}\nbait: {Describe(preset.BaitIds)}");
            }

            ImGui.TableNextColumn();
            DrawTargetPicker(preset);

            ImGui.TableNextColumn();
            var picked = chosen.TryGetValue(preset.Name, out var fishId) && fishId != 0;
            var already = picked && existingIds.Contains(fishId.ToString());

            if (preset.IsIdNamed)
                ImGui.TextColored(Good, "GBR-ready");
            else if (already)
                ImGui.TextColored(Warn, $"{fishId} exists");
            else if (picked)
                ImGui.TextDisabled("ready");
            else
                ImGui.TextColored(Warn, "pick a fish");

            ImGui.TableNextColumn();
            var canCreate = picked && !already && !preset.IsIdNamed;
            if (!canCreate)
                ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Create##{preset.Name}"))
                Create(preset, fishId);
            if (!canCreate)
                ImGui.EndDisabled();
        }

        ImGui.EndTable();
    }

    private static string Describe(int[] ids)
        => ids.Length == 0 ? "-" : string.Join(", ", ids.Select(i => $"{FishIndex.NameOf((uint)i)} ({i})"));

    private void DrawTargetPicker(AutoHookPresets.PresetInfo preset)
    {
        if (preset.IsIdNamed)
        {
            ImGui.TextDisabled(uint.TryParse(preset.Name, out var own) ? FishIndex.NameOf(own) : "");
            return;
        }

        chosen.TryGetValue(preset.Name, out var current);
        var label = current != 0 ? $"{FishIndex.NameOf(current)} ({current})" : "Select fish...";
        var popupId = $"pick##{preset.Name}";

        ImGui.SetNextItemWidth(-1);
        if (ImGui.Button($"{label}##btn{preset.Name}", new Vector2(-1, 0)))
        {
            if (!searches.ContainsKey(preset.Name))
                searches[preset.Name] = "";
            ImGui.OpenPopup(popupId);
        }

        if (!ImGui.BeginPopup(popupId))
            return;

        var search = searches.TryGetValue(preset.Name, out var s) ? s : "";
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint($"##search{preset.Name}", "Search fish by name...", ref search, 64))
            searches[preset.Name] = search;

        if (current != 0 && ImGui.Selectable("(clear selection)"))
        {
            chosen.Remove(preset.Name);
            ImGui.CloseCurrentPopup();
        }

        foreach (var (name, id) in FishIndex.Search(search))
        {
            if (!ImGui.Selectable($"{name} ({id})##{preset.Name}-{id}"))
                continue;
            chosen[preset.Name] = id;
            ImGui.CloseCurrentPopup();
        }

        if (search.Length >= 2 && FishIndex.Search(search).Count == 0)
            ImGui.TextDisabled("No items match.");

        ImGui.EndPopup();
    }

    private void DrawFooter()
    {
        var existingIds = presets.Where(p => p.IsIdNamed).Select(p => p.Name).ToHashSet();
        var pending = presets
            .Where(p => !p.IsIdNamed && chosen.TryGetValue(p.Name, out var id) && id != 0
                        && !existingIds.Contains(id.ToString()))
            .ToList();

        // Two presets pointing at one fish would fight over the same name.
        var clashes = pending.GroupBy(p => chosen[p.Name]).Where(g => g.Count() > 1).ToList();
        if (clashes.Count > 0)
        {
            ImGui.TextColored(Bad, $"{clashes.Count} fish picked by more than one preset - only one preset can own an id.");
            return;
        }

        if (pending.Count == 0)
        {
            ImGui.TextDisabled("Nothing pending - pick a target fish for the presets you want GBR to use.");
            return;
        }

        if (!ImGui.Button($"Create {pending.Count} id-named preset(s)"))
            return;

        var made = 0;
        foreach (var preset in pending)
        {
            if (Create(preset, chosen[preset.Name], reload: false))
                made++;
        }

        Reload();
        status = $"Created {made} preset(s). Turn on \"Use existing AutoHook presets\" in GatherBuddy Reborn.";
    }

    private bool Create(AutoHookPresets.PresetInfo preset, uint fishId, bool reload = true)
    {
        if (!AutoHookPresets.CreateIdNamedCopy(preset, fishId, out var error))
        {
            status = $"Failed on \"{preset.Name}\": {error}";
            return false;
        }

        if (reload)
        {
            Reload();
            status = $"Created \"{fishId}\" from \"{preset.Name}\".";
        }

        return true;
    }

    #endregion
}
