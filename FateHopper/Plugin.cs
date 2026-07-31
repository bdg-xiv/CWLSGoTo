using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using System;
using System.Linq;
using System.Numerics;

namespace FateHopper;

/// <summary>
/// A clickable list of the active FATEs in the Occult Crescent's South Horn. Clicking one
/// teleports to the aetheryte shard nearest to that FATE, through Lifestream.
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

    private const ushort SouthHornTerritory = 1252;

    /// <summary>How close counts as "standing at" a shard - the interaction range the
    /// aethernet itself uses.</summary>
    private const float ShardRange = 4.5f;

    private sealed record Shard(string Name, uint PlaceNameId, Vector3 Position);

    // South Horn's aethernet, keyed by PlaceName row id - the id Lifestream's aethernet
    // teleport takes. Positions verified against BOCCHI's zone data.
    private static readonly Shard[] Shards =
    [
        new("Base Camp", 4944, new Vector3(830.75f, 72.98f, -695.98f)),
        new("The Wanderer's Haven", 4936, new Vector3(-173.02f, 8.19f, -611.14f)),
        new("Crystallized Caverns", 4929, new Vector3(-358.14f, 101.98f, -120.96f)),
        new("Eldergrowth", 4930, new Vector3(306.94f, 105.18f, 305.65f)),
        new("Stonemarsh", 4942, new Vector3(-384.12f, 99.2f, 281.42f)),
    ];

    private readonly Configuration config;
    private bool windowOpen;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => windowOpen = !windowOpen)
        {
            HelpMessage = "Shows the South Horn FATE list; click a FATE to shard-hop toward it.",
        });

        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;

        windowOpen = config.AutoOpen && InSouthHorn;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= Draw;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private static bool InSouthHorn => Svc.ClientState.TerritoryType == SouthHornTerritory;

    private void ToggleWindow() => windowOpen = !windowOpen;

    private void OnTerritoryChanged(uint territory)
    {
        if (config.AutoOpen)
            windowOpen = territory == SouthHornTerritory;
    }

    private static Shard NearestShardTo(Vector3 position)
        => Shards.OrderBy(s => Vector3.Distance(s.Position, position)).First();

    private void Hop(IFate fate)
    {
        var destination = NearestShardTo(fate.Position);
        var name = fate.Name.TextValue;

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        if (Vector3.Distance(player.Position, destination.Position) <= ShardRange)
        {
            Svc.Chat.Print($"[FateHopper] You're already at {destination.Name} - it's the shard nearest to {name}.");
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
            Svc.Chat.Print($"[FateHopper] Teleporting to {destination.Name} for {name}.");
        else
            Svc.Chat.PrintError($"[FateHopper] Lifestream refused the teleport to {destination.Name}.");
    }

    private void Draw()
    {
        if (!windowOpen || !InSouthHorn)
            return;

        ImGui.SetNextWindowSize(new Vector2(430, 320), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("FATE Hopper###FateHopper", ref windowOpen))
        {
            DrawFateList();
            ImGui.Separator();
            DrawFooter();
        }

        ImGui.End();
    }

    private void DrawFateList()
    {
        // Running FATEs first, most urgent at the top; the ones still preparing follow.
        var fates = Svc.Fates
            .Where(f => f is { } && f.State is FateState.Running or FateState.Preparing)
            .OrderBy(f => f.State == FateState.Running ? 0 : 1)
            .ThenBy(f => f.TimeRemaining)
            .ToList();

        if (fates.Count == 0)
        {
            ImGui.TextDisabled("No FATEs up right now.");
            return;
        }

        if (!ImGui.BeginTable("###FateTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("FATE", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("Shard", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();

        foreach (var fate in fates)
        {
            var shard = NearestShardTo(fate.Position);
            var run = Vector3.Distance(shard.Position, fate.Position);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var label = fate.HasBonus ? $"{fate.Name.TextValue} ★" : fate.Name.TextValue;
            if (ImGui.Selectable($"{label}###fate{fate.FateId}", false, ImGuiSelectableFlags.SpanAllColumns))
                Hop(fate);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Teleport to {shard.Name}, {run:0} yalms from the FATE."
                    + (fate.HasBonus ? "\n★ bonus FATE" : ""));

            ImGui.TableNextColumn();
            ImGui.Text($"{fate.Level}");

            ImGui.TableNextColumn();
            if (fate.State == FateState.Preparing)
                ImGui.TextDisabled("soon");
            else if (fate.Progress > 0)
                ImGui.Text($"{fate.Progress}%%");
            else
                ImGui.Text(FormatTime(fate.TimeRemaining));

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{shard.Name} ({run:0}y)");
        }

        ImGui.EndTable();
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
            ImGui.SetTooltip("Open this window on entering South Horn, close it on leaving.");
    }

    private static string FormatTime(long seconds)
    {
        if (seconds <= 0)
            return "-";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}" : $"{span.Seconds}s";
    }
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
