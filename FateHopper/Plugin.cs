using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static ECommons.GenericHelpers;

namespace FateHopper;

/// <summary>
/// A clickable list of the active FATEs in the Occult Crescent. Clicking one teleports to
/// the aetheryte shard nearest to that FATE, through Lifestream.
///
/// The shard network only works from within range of a shard - the same rule as using one
/// by hand - so this doesn't move the character to a shard first; it just does the
/// targeting and destination-picking. Lifestream's aethernet IPC enforces the range.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/fatehopper";

    /// <summary>How close counts as "standing at" a shard - the interaction range the
    /// aethernet itself uses.</summary>
    private const float ShardRange = 4.5f;

    /// <summary>A shard's map position (X and Z; the network doesn't care about height)
    /// and the PlaceName row Lifestream's aethernet teleport takes. The display name
    /// comes from that same row, so it can't drift from what Lifestream matches on.</summary>
    private sealed record Shard(uint PlaceNameId, Vector2 Position);

    // Positions and PlaceName ids lifted from Lifestream's own custom-aethernet registry
    // - it is the thing executing the teleport, so its table is the one that counts.
    private static readonly Dictionary<uint, Shard[]> ZoneShards = new()
    {
        // South Horn
        [1252] =
        [
            new(4944, new Vector2(830.7f, -696.0f)),  // Expedition Base Camp
            new(4928, new Vector2(-173.0f, -611.1f)), // The Wanderer's Haven
            new(4929, new Vector2(-358.1f, -121.0f)), // Crystallized Caverns
            new(4930, new Vector2(306.9f, 305.7f)),   // Eldergrowth
            new(4947, new Vector2(-384.1f, 281.4f)),  // Stonemarsh
        ],
        // North Horn
        [1346] =
        [
            new(5571, new Vector2(880.0f, 880.1f)),   // North Horn Base Camp
            new(5576, new Vector2(451.7f, 528.8f)),   // The Crown of Karnak
            new(5572, new Vector2(357.7f, -554.3f)),  // Sinking Sanctuary
            new(5573, new Vector2(-547.2f, 594.4f)),  // Suspended Masonry
            new(5574, new Vector2(-388.6f, -440.5f)), // Moldering Outskirts
            new(5575, new Vector2(-13.7f, -40.5f)),   // Unhallowed Hamlet
        ],
    };

    /// <summary>A pot FATE's fixed spawn point. The point of the buttons is going there
    /// BEFORE the FATE exists, when there is nothing live to click in the list.</summary>
    private sealed record Pot(string Label, Vector3 Position);

    // Spawn points captured by the community occult trackers (AOCCH's data, matching
    // BOCCHI's for South Horn); the north/south split is the one the trackers use.
    private static readonly Dictionary<uint, (Pot North, Pot South)> ZonePots = new()
    {
        [1252] = (new Pot("Persistent Pots", new Vector3(200.0f, 111.7f, -215.0f)),
                  new Pot("Pleading Pots", new Vector3(-481.0f, 75.0f, 528.0f))),
        [1346] = (new Pot("Daylight Pottery", new Vector3(233.0f, 7.7f, -470.0f)),
                  new Pot("In a Pot of Bother", new Vector3(-505.3f, 53.1f, 244.0f))),
    };

    private readonly Configuration config;
    private bool windowOpen;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shows the Occult Crescent FATE list; click a FATE to shard-hop toward it. "
                + "\"/fatehopper return\" presses Return and accepts its confirmation, "
                + "\"/fatehopper buffs\" runs the Freelancer buff errand - both macro-friendly.",
        });

        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        Svc.Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;

        windowOpen = config.AutoOpen && CurrentShards != null;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= Draw;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    /// <summary>The current zone's shards, or null anywhere outside the Occult Crescent.</summary>
    private static Shard[]? CurrentShards
        => ZoneShards.GetValueOrDefault(Svc.ClientState.TerritoryType);

    private void ToggleWindow() => windowOpen = !windowOpen;

    /// <summary>Bare /fatehopper toggles the window; the arguments exist so the two
    /// errands can live on a hotbar as game macros.</summary>
    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "return":
                StartReturn();
                break;
            case "buffs":
                StartBuffs();
                break;
            default:
                windowOpen = !windowOpen;
                break;
        }
    }

    /// <summary>The errands only mean anything inside the Occult Crescent - and outside
    /// it, Return is the ordinary teleport home, which a macro must never press.</summary>
    private static bool InOccultCrescent => ZoneShards.ContainsKey(Svc.ClientState.TerritoryType);

    private void OnTerritoryChanged(uint territory)
    {
        if (config.AutoOpen)
            windowOpen = ZoneShards.ContainsKey(territory);
    }

    private static string NameOf(Shard shard)
        => Svc.Data.GetExcelSheet<PlaceName>().GetRowOrDefault(shard.PlaceNameId)?.Name.ExtractText() ?? "?";

    /// <summary>Horizontal distance - the network and the map both ignore height.</summary>
    private static float Distance(Vector2 shardPosition, Vector3 worldPosition)
        => Vector2.Distance(shardPosition, new Vector2(worldPosition.X, worldPosition.Z));

    private static Shard NearestShardTo(Shard[] shards, Vector3 position)
        => shards.OrderBy(s => Distance(s.Position, position)).First();

    /// <summary>Straight-line nearest is wrong where the geography disagrees: Eye to
    /// Eye's spawn is closest to Unhallowed Hamlet as the crow flies, but that shard
    /// stands on the lake island, so the run is longer than from Sinking Sanctuary on
    /// the shore. These pins come from AOCCH's fate data (the South Horn pair matches
    /// BOCCHI's), keyed by fate id to the shard's PlaceName row.</summary>
    private static readonly Dictionary<uint, uint> FateShardOverrides = new()
    {
        [1965] = 4928, // The Winged Terror -> The Wanderer's Haven
        [1967] = 4929, // Brain Drain -> Crystallized Caverns
        [2075] = 5572, // Eye to Eye -> Sinking Sanctuary; the middle shard is on the island
    };

    private static Shard DestinationFor(Shard[] shards, uint? fateId, Vector3 position)
    {
        if (fateId is { } id && FateShardOverrides.TryGetValue(id, out var placeName)
            && shards.FirstOrDefault(s => s.PlaceNameId == placeName) is { } pinned)
            return pinned;

        return NearestShardTo(shards, position);
    }

    /// <summary>A critical encounter, snapshotted out of the game's dynamic-event
    /// container so no pointer outlives the read.</summary>
    private sealed record Ce(ushort Id, string Name, DynamicEventState State, byte Progress, uint SecondsLeft, Vector3 Position);

    /// <summary>The zone's critical encounters. They live in the Occult Crescent's
    /// content director, not the FATE table, which is why the FATE list alone misses
    /// them.</summary>
    private static unsafe List<Ce> ReadCriticalEncounters()
    {
        var list = new List<Ce>();

        var director = PublicContentOccultCrescent.GetInstance();
        if (director == null)
            return list;

        foreach (ref var ev in director->DynamicEventContainer.Events)
        {
            if (ev.State == DynamicEventState.Inactive)
                continue;

            // The Forked Tower's event sits in the container permanently, cycle after
            // cycle, and would show as a ghost row. Ordinary critical encounters have
            // EventType below 4 - the same cut BOCCHI makes.
            if (ev.EventType >= 4)
                continue;

            var name = ev.Name.ToString();
            if (name.Length == 0)
                continue;

            list.Add(new Ce(ev.DynamicEventId, name, ev.State, ev.Progress, ev.SecondsLeft, ev.MapMarker.Position));
        }

        return list;
    }

    private void Hop(Shard destination, string name)
    {
        var destinationName = NameOf(destination);

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        if (Distance(destination.Position, player.Position) <= ShardRange)
        {
            Svc.Chat.Print($"[FateHopper] You're already at {destinationName} - it's the shard nearest to {name}.");
            return;
        }

        if (LifestreamIpc.ActiveCustomAetheryte() is not { } active)
        {
            Svc.Chat.PrintError("[FateHopper] Lifestream isn't loaded - it does the actual shard teleport.");
            return;
        }

        // The aethernet only answers within range of a shard; say so instead of letting
        // the request die quietly.
        if (active == 0)
        {
            Svc.Chat.Print($"[FateHopper] Stand within range of any aetheryte shard first, then click {name} again.");
            return;
        }

        if (LifestreamIpc.IsBusy() == true)
        {
            Svc.Chat.Print("[FateHopper] Lifestream is busy with another trip - try again in a moment.");
            return;
        }

        if (LifestreamIpc.AethernetTeleport(destination.PlaceNameId) == true)
            Svc.Chat.Print($"[FateHopper] Teleporting to {destinationName} for {name}.");
        else
            Svc.Chat.PrintError($"[FateHopper] Lifestream refused the teleport to {destinationName}.");
    }

    private void Draw()
    {
        if (!windowOpen || CurrentShards is not { } shards)
            return;

        ImGui.SetNextWindowSize(new Vector2(430, 320), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("FATE Hopper###FateHopper", ref windowOpen))
        {
            DrawFateList(shards);
            ImGui.Separator();
            DrawPotButtons(shards);
            DrawFooter();
        }

        ImGui.End();
    }

    // ---- Freelancer buff run ----------------------------------------------------
    //
    // Freelancer's Inquiring Mind applies every phantom buff in one press, so the whole
    // errand is: remember the current phantom job, swap to Freelancer, press it, swap
    // back. Each step is a server round-trip, so this runs as a little state machine on
    // the framework tick rather than a blocking loop.

    private enum BuffStep { Idle, Dismounting, SwappingIn, UsingAction, WaitingBuffs, SwappingBack }

    private const byte FreelancerJobId = 0;
    private const uint InquiringMindActionId = 46606;
    private const uint DismountGeneralActionId = 23;

    /// <summary>The statuses Inquiring Mind grants (Enduring Fortitude, Fleetfooted,
    /// Romeo's Ballad, Quick Step) - seeing any of them land is the proof the press
    /// worked before the job is swapped away again.</summary>
    private static readonly uint[] PhantomBuffStatusIds = [4233, 4239, 4244, 4799];

    private BuffStep buffStep = BuffStep.Idle;
    private byte buffReturnJob;
    private long buffDeadline;

    private unsafe void StartBuffs()
    {
        if (buffStep != BuffStep.Idle)
            return;

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            Svc.Chat.Print("[FateHopper] Only inside the Occult Crescent.");
            return;
        }

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            Svc.Chat.Print("[FateHopper] Not in combat - the buff swap needs a calm moment.");
            return;
        }

        buffReturnJob = state->CurrentSupportJob;
        buffStep = Svc.Condition[ConditionFlag.Mounted] ? BuffStep.Dismounting : BuffStep.SwappingIn;
        buffDeadline = Environment.TickCount64 + 8000;
        Svc.Chat.Print("[FateHopper] Swapping to Freelancer for buffs...");
    }

    private void FailBuffs(string reason)
    {
        Svc.Chat.PrintError($"[FateHopper] {reason}");

        // Never leave the character stranded on Freelancer: if the swap in happened,
        // queue the swap back rather than giving up outright.
        if (buffStep is BuffStep.UsingAction or BuffStep.WaitingBuffs && buffReturnJob != FreelancerJobId)
        {
            buffStep = BuffStep.SwappingBack;
            buffDeadline = Environment.TickCount64 + 8000;
            return;
        }

        buffStep = BuffStep.Idle;
    }

    // ---- Return -----------------------------------------------------------------
    //
    // The plain Return action, plus a one-shot accept for its confirmation. YesAlready
    // can't be used for this: it would also accept the release prompt on death. Here
    // nothing is armed unless the button was just pressed, the arming lasts ten
    // seconds, and even then only a dialog carrying the "starting point" wording is
    // answered - a death release inside the window is left alone.

    private const uint ReturnGeneralActionId = 8;
    private const string ReturnPromptFragment = "starting point";

    private long returnArmedUntil;

    private unsafe void StartReturn()
    {
        if (!InOccultCrescent)
        {
            Svc.Chat.Print("[FateHopper] Only inside the Occult Crescent - out here Return would teleport you home.");
            return;
        }

        var player = Svc.Objects.LocalPlayer;
        if (player == null || player.IsDead)
        {
            Svc.Chat.Print("[FateHopper] Not while dead - the release prompt stays yours to answer.");
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager->GetActionStatus(ActionType.GeneralAction, ReturnGeneralActionId) != 0
            || !actionManager->UseAction(ActionType.GeneralAction, ReturnGeneralActionId))
        {
            Svc.Chat.Print("[FateHopper] Return isn't available right now (combat or cooldown).");
            return;
        }

        returnArmedUntil = Environment.TickCount64 + 10000;
        Svc.Chat.Print("[FateHopper] Returning to the starting point...");
    }

    private void TickReturn()
    {
        if (returnArmedUntil == 0)
            return;

        if (Environment.TickCount64 > returnArmedUntil)
        {
            returnArmedUntil = 0;
            return;
        }

        // Only the return confirmation; any other dialog that pops inside the window
        // is somebody else's business.
        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno)
            && yesno.IsAddonReady
            && yesno.Text.Contains(ReturnPromptFragment, StringComparison.OrdinalIgnoreCase)
            && EzThrottler.Throttle("FateHopper.ReturnYes", 500))
        {
            yesno.Yes();
            returnArmedUntil = 0;
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        TickReturn();

        if (buffStep == BuffStep.Idle)
            return;

        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
        {
            buffStep = BuffStep.Idle;
            return;
        }

        var now = Environment.TickCount64;
        if (now > buffDeadline)
        {
            switch (buffStep)
            {
                case BuffStep.SwappingIn:
                    FailBuffs("Couldn't swap to Freelancer - stand near the knowledge crystal and try again.");
                    break;
                case BuffStep.SwappingBack:
                    Svc.Chat.PrintError("[FateHopper] Couldn't swap back - change your phantom job at the crystal yourself.");
                    buffStep = BuffStep.Idle;
                    break;
                default:
                    FailBuffs("The buff run timed out.");
                    break;
            }
            return;
        }

        var actionManager = ActionManager.Instance();

        switch (buffStep)
        {
            case BuffStep.Dismounting:
                if (!Svc.Condition[ConditionFlag.Mounted])
                {
                    buffStep = BuffStep.SwappingIn;
                    buffDeadline = now + 8000;
                }
                else if (EzThrottle("dismount"))
                    actionManager->UseAction(ActionType.GeneralAction, DismountGeneralActionId);
                break;

            case BuffStep.SwappingIn:
                if (state->CurrentSupportJob == FreelancerJobId)
                {
                    buffStep = BuffStep.UsingAction;
                    buffDeadline = now + 6000;
                }
                else if (EzThrottle("swapin"))
                    PublicContentOccultCrescent.ChangeSupportJob(FreelancerJobId);
                break;

            case BuffStep.UsingAction:
                // The action needs a beat after the job change before the game accepts it.
                if (actionManager->GetActionStatus(ActionType.Action, InquiringMindActionId) == 0
                    && EzThrottle("action")
                    && actionManager->UseAction(ActionType.Action, InquiringMindActionId))
                {
                    buffStep = BuffStep.WaitingBuffs;
                    buffDeadline = now + 5000;
                }
                break;

            case BuffStep.WaitingBuffs:
                if (HasAnyPhantomBuff())
                {
                    if (buffReturnJob == FreelancerJobId)
                    {
                        Svc.Chat.Print("[FateHopper] Buffs up.");
                        buffStep = BuffStep.Idle;
                    }
                    else
                    {
                        Svc.Chat.Print("[FateHopper] Buffs up - swapping back.");
                        buffStep = BuffStep.SwappingBack;
                        buffDeadline = now + 8000;
                    }
                }
                break;

            case BuffStep.SwappingBack:
                if (state->CurrentSupportJob == buffReturnJob)
                {
                    Svc.Chat.Print("[FateHopper] Back on your phantom job.");
                    buffStep = BuffStep.Idle;
                }
                else if (EzThrottle("swapback"))
                    PublicContentOccultCrescent.ChangeSupportJob(buffReturnJob);
                break;
        }
    }

    private static bool HasAnyPhantomBuff()
        => Svc.Objects.LocalPlayer?.StatusList.Any(s => PhantomBuffStatusIds.Contains(s.StatusId)) == true;

    private long lastBuffAttemptAt;

    /// <summary>One attempt per second within a state, so a slow server answer isn't
    /// spammed with repeats.</summary>
    private bool EzThrottle(string _)
    {
        var now = Environment.TickCount64;
        if (now - lastBuffAttemptAt < 1000)
            return false;
        lastBuffAttemptAt = now;
        return true;
    }

    /// <summary>Two fixed buttons for camping the pot FATEs ahead of their spawn - one
    /// per spawn point, north and south - plus the Freelancer buff errand.</summary>
    private void DrawPotButtons(Shard[] shards)
    {
        if (ZonePots.TryGetValue(Svc.ClientState.TerritoryType, out var pots))
        {
            ImGui.TextDisabled("Camp a pot:");
            ImGui.SameLine();
            PotButton(shards, "North", pots.North);
            ImGui.SameLine();
            PotButton(shards, "South", pots.South);
            ImGui.SameLine();
        }

        ImGui.BeginDisabled(buffStep != BuffStep.Idle);
        if (ImGui.SmallButton(buffStep == BuffStep.Idle ? "Buffs" : "Buffs..."))
            StartBuffs();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Swaps to Freelancer, uses Inquiring Mind (every phantom buff in one press),\n"
                + "and swaps back to the phantom job you were on.\n"
                + "Phantom jobs can only be changed near the knowledge crystal.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Return"))
            StartReturn();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Presses Return and accepts its confirmation - and only that one.\n"
                + "Nothing is armed unless this button was just pressed, so the\n"
                + "release prompt when you die is never touched.");
    }

    private void PotButton(Shard[] shards, string direction, Pot pot)
    {
        if (ImGui.SmallButton($"{direction}###pot{direction}"))
            Hop(NearestShardTo(shards, pot.Position), $"{pot.Label} ({direction.ToLowerInvariant()} pot)");

        if (ImGui.IsItemHovered())
        {
            var shard = NearestShardTo(shards, pot.Position);
            ImGui.SetTooltip($"{pot.Label} spawns here.\nTeleport to {NameOf(shard)}, "
                + $"{Distance(shard.Position, pot.Position):0} yalms from the spawn point.");
        }
    }

    private static readonly Vector4 CeColor = new(1f, 0.85f, 0.4f, 1f);

    private void DrawFateList(Shard[] shards)
    {
        // Critical encounters on top - they're rarer and on a recruitment timer - then
        // running FATEs, most urgent first; the ones still preparing follow.
        var encounters = ReadCriticalEncounters();
        var fates = Svc.Fates
            .Where(f => f is { } && f.State is FateState.Running or FateState.Preparing)
            .OrderBy(f => f.State == FateState.Running ? 0 : 1)
            .ThenBy(f => f.TimeRemaining)
            .ToList();

        if (encounters.Count == 0 && fates.Count == 0)
        {
            ImGui.TextDisabled("No FATEs or critical encounters up right now.");
            return;
        }

        if (!ImGui.BeginTable("###FateTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("FATE / CE", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Shard", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();

        foreach (var ce in encounters)
            DrawRow(shards, $"ce{ce.Id}", ce.Name, ce.Position, ceState: ce);

        foreach (var fate in fates)
            DrawRow(shards, $"fate{fate.FateId}", fate.Name.TextValue, fate.Position, fate: fate);

        ImGui.EndTable();
    }

    private void DrawRow(Shard[] shards, string id, string name, Vector3 position, IFate? fate = null, Ce? ceState = null)
    {
        var pinned = fate != null && FateShardOverrides.ContainsKey(fate.FateId);
        var shard = DestinationFor(shards, fate?.FateId, position);
        var shardName = NameOf(shard);
        var run = Distance(shard.Position, position);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var label = fate is { HasBonus: true } ? $"{name} ★" : name;
        if (ceState != null)
            ImGui.PushStyleColor(ImGuiCol.Text, CeColor);
        var clicked = ImGui.Selectable($"{label}###{id}", false, ImGuiSelectableFlags.SpanAllColumns);
        if (ceState != null)
            ImGui.PopStyleColor();
        if (clicked)
            Hop(shard, name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Teleport to {shardName}, {run:0} yalms away."
                + (pinned ? "\nFixed route - the closer shard is cut off by terrain." : "")
                + (ceState != null ? "\nCritical encounter" : "")
                + (fate is { HasBonus: true } ? "\n★ bonus FATE" : ""));

        ImGui.TableNextColumn();
        if (ceState != null)
            ImGui.TextColored(CeColor, "CE");
        else
            ImGui.Text($"{fate!.Level}");

        ImGui.TableNextColumn();
        if (ceState != null)
        {
            // Register is the window that matters: it's how long is left to sign up.
            switch (ceState.State)
            {
                case DynamicEventState.Register:
                    ImGui.TextColored(CeColor, FormatTime(ceState.SecondsLeft));
                    break;
                case DynamicEventState.Warmup:
                    ImGui.TextDisabled("soon");
                    break;
                default:
                    ImGui.Text(ceState.Progress > 0 ? $"{ceState.Progress}%" : FormatTime(ceState.SecondsLeft));
                    break;
            }
        }
        else if (fate!.State == FateState.Preparing)
            ImGui.TextDisabled("soon");
        else if (fate.Progress > 0)
            ImGui.Text($"{fate.Progress}%");
        else
            ImGui.Text(FormatTime(fate.TimeRemaining));

        ImGui.TableNextColumn();
        ImGui.TextDisabled($"{shardName} ({run:0}y)");
    }

    private void DrawFooter()
    {
        var inRange = LifestreamIpc.ActiveCustomAetheryte() is { } active && active != 0;
        ImGui.TextColored(inRange ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.75f, 0.3f, 1f),
            inRange ? "In shard range - click a FATE to travel." : "Stand near a shard to enable travel.");

        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 70);
        var autoOpen = config.AutoOpen;
        if (ImGui.Checkbox("Auto", ref autoOpen))
        {
            config.AutoOpen = autoOpen;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open this window on entering the Occult Crescent, close it on leaving.");
    }

    private static string FormatTime(long seconds)
    {
        if (seconds <= 0)
            return "-";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}" : $"{span.Seconds}s";
    }

    private static string FormatTime(uint seconds) => FormatTime((long)seconds);
}

/// <summary>Lifestream's aethernet IPC. Null means Lifestream couldn't be reached, which
/// is a different thing from it answering "no".</summary>
internal static class LifestreamIpc
{
    public static bool? IsBusy() => Call<bool>("Lifestream.IsBusy");

    public static uint? ActiveCustomAetheryte() => Call<uint>("Lifestream.GetActiveCustomAetheryte");

    public static bool? AethernetTeleport(uint placeNameId)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId")
                .InvokeFunc(placeNameId);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Lifestream aethernet teleport unavailable: {ex.Message}");
            return null;
        }
    }

    private static T? Call<T>(string name) where T : struct
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc();
        }
        catch
        {
            return null;
        }
    }
}
