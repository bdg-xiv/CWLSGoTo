using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace Hindsight;

/// <summary>
/// Cooldown timers while watching a Duty Recorder replay. The game never simulates
/// recast state during playback - hotbars stay hidden - so this rebuilds it: every
/// action the replay makes a player use is caught as its packet replays, matched to its
/// recast group, and drawn as a countdown. The timers run on the replay's own clock, so
/// pause, speed, and chapter skips behave; a jump backwards starts the ledger over.
/// Works on any replay the client can play, including files other people recorded.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/hindsight";

    private static readonly Vector4 ReadyGreen = new(0.35f, 0.85f, 0.45f, 1f);

    private readonly Configuration config;
    private readonly Tracker tracker = new();
    private readonly Hook<ActionEffectHandler.Delegates.Receive> onActionUsed;

    private bool windowOpen;
    private bool watching;
    private float lastPosition;
    private uint chosenPlayer;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        onActionUsed = Svc.Hook.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            (nint)ActionEffectHandler.Addresses.Receive.Value, OnActionUsed);
        onActionUsed.Enable();

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles the replay cooldown window. It also opens by itself when a duty replay starts.",
        });

        Svc.Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += Toggle;
        PluginInterface.UiBuilder.OpenMainUi += Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        PluginInterface.UiBuilder.Draw -= Draw;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Commands.RemoveHandler(CommandName);
        onActionUsed.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args) => Toggle();

    private void Toggle() => windowOpen = !windowOpen;

    private static bool Watching => Svc.Condition[ConditionFlag.DutyRecorderPlayback];

    /// <summary>Current playback position in seconds. ClientStructs labels the field
    /// PositionMs, but the game keeps seconds in it - every working consumer stores
    /// milliseconds divided by 1000 - so the label loses to the evidence.</summary>
    private static float Position
    {
        get
        {
            var replay = ContentsReplayManager.Instance();
            return replay == null ? 0f : replay->PositionMs;
        }
    }

    private void OnActionUsed(uint casterEntityId, Character* caster, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetIds)
    {
        onActionUsed.Original(casterEntityId, caster, targetPos, header, effects, targetIds);
        try
        {
            if (!Watching || header->ActionType != 1)
                return;
            if (Svc.Objects.SearchByEntityId(casterEntityId) is not IPlayerCharacter who)
                return;

            // For real actions SpellId is the one that resolved; ActionId is the backstop.
            uint actionId = header->SpellId != 0 ? header->SpellId : header->ActionId;
            tracker.NoteUse(casterEntityId, who.Name.TextValue, (byte)who.ClassJob.RowId,
                actionId, Position, config.MinRecastSeconds);
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "Hindsight could not note a replayed action");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!Watching)
        {
            if (watching)
            {
                watching = false;
                windowOpen = false;
                chosenPlayer = 0;
                lastPosition = 0f;
                tracker.Forget();
            }
            return;
        }

        if (!watching)
        {
            watching = true;
            if (config.AutoOpen)
                windowOpen = true;
        }

        var position = Position;
        if (position + 1f < lastPosition)
            tracker.Forget(); // jumped backwards: the replay restarts, and so do we
        lastPosition = position;
    }

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(360, 400) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Hindsight", ref windowOpen))
        {
            if (Watching)
                DrawPlayback();
            else
                ImGui.TextWrapped("Waiting for a duty replay. During playback, everyone's used actions "
                    + "show up here with their cooldown timers rebuilt from the recording.");
            DrawSettings();
        }
        ImGui.End();
    }

    private void DrawPlayback()
    {
        var position = Position;

        var seconds = (int)Math.Max(0f, position);
        var line = $"{seconds / 60}:{seconds % 60:00}";
        var replay = ContentsReplayManager.Instance();
        if (replay != null && (replay->PlaybackControls & ContentsReplayPlaybackControl.Paused) != 0)
            line += "  ·  paused";
        else if (replay != null && Math.Abs(replay->PlaybackSpeed - 1f) > 0.01f)
            line += $"  ·  {replay->PlaybackSpeed:0.##}x";
        ImGui.TextDisabled(line);

        if (tracker.Players.Count == 0)
        {
            ImGui.TextWrapped("Nothing seen yet - timers appear as people use their actions.");
            return;
        }

        var recorder = Svc.Objects.LocalPlayer?.EntityId ?? 0u;
        var current = chosenPlayer != 0 && tracker.Players.ContainsKey(chosenPlayer) ? chosenPlayer
            : tracker.Players.ContainsKey(recorder) ? recorder
            : tracker.Players.Keys.First();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##who", Label(tracker.Players[current], current == recorder)))
        {
            foreach (var (id, player) in tracker.Players.OrderBy(p => p.Value.Name, StringComparer.Ordinal))
            {
                if (ImGui.Selectable(Label(player, id == recorder), id == current))
                    chosenPlayer = id;
            }
            ImGui.EndCombo();
        }

        DrawCooldowns(tracker.Players[current], position);
    }

    private static string Label(Tracker.Player player, bool isRecorder)
        => $"{player.Name} ({JobAbbreviation(player.Job)}{(isRecorder ? ", pov" : "")})";

    private static string JobAbbreviation(byte job)
        => Svc.Data.GetExcelSheet<ClassJob>().TryGetRow(job, out var row) ? row.Abbreviation.ExtractText() : $"job {job}";

    private static void DrawCooldowns(Tracker.Player player, float now)
    {
        var iconSize = ImGui.GetFrameHeight();
        if (!ImGui.BeginTable("cooldowns", 3, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("icon", ImGuiTableColumnFlags.WidthFixed, iconSize);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("timer", ImGuiTableColumnFlags.WidthFixed, 130 * ImGuiHelpers.GlobalScale);

        foreach (var cd in player.Groups.Values
            .OrderByDescending(c => c.RechargeSeconds * c.MaxCharges)
            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Image(Svc.Texture.GetFromGameIcon(new GameIconLookup(cd.Icon)).GetWrapOrEmpty().Handle,
                new Vector2(iconSize));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(cd.Name);

            ImGui.TableNextColumn();
            var charges = cd.ChargesAt(now);
            if (charges >= cd.MaxCharges)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ReadyGreen, "ready");
                var idle = now - cd.FullAtSeconds;
                if (idle >= 1f)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"+{idle:0}s"); // how long it has sat unused
                }
            }
            else
            {
                var whole = (int)charges;
                var toNext = (1f - (charges - whole)) * cd.RechargeSeconds;
                var text = toNext >= 9.95f ? $"{toNext:0}s" : $"{toNext:0.0}s";
                if (cd.MaxCharges > 1)
                    text = $"{whole}/{cd.MaxCharges}  {text}";
                ImGui.ProgressBar(charges - whole, new Vector2(-1, iconSize * 0.85f), text);
            }
        }

        ImGui.EndTable();
    }

    private void DrawSettings()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Settings"))
            return;

        var dirty = false;

        var autoOpen = config.AutoOpen;
        if (ImGui.Checkbox("Open by itself when a replay starts", ref autoOpen))
        {
            config.AutoOpen = autoOpen;
            dirty = true;
        }

        var minRecast = config.MinRecastSeconds;
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Ignore cooldowns shorter than", ref minRecast, 2.5f, 60f, "%.1fs"))
        {
            config.MinRecastSeconds = minRecast;
            dirty = true;
        }

        if (ImGui.Button("Forget everything seen"))
            tracker.Forget();

        if (dirty)
            config.Save();
    }
}
