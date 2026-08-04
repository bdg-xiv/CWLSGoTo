using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace HuntSpots;

internal sealed class MainWindow : Window
{
    private readonly Configuration config;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Configuration config)
        : base("Hunt Spots###HuntSpots")
    {
        this.config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 230),
            MaximumSize = new Vector2(700, 800),
        };
    }

    private void DrawRank(string label, string rank, Func<bool> shown, Action<bool> setShown,
        Func<uint> icon, Action<uint> setIcon, uint fallback)
    {
        ImGui.PushID(rank);

        var on = shown();
        if (ImGui.Checkbox(label, ref on))
        {
            setShown(on);
            config.Save();
        }

        if (on)
        {
            ImGui.SameLine();
            var value = (int)icon();
            ImGui.SetNextItemWidth(90f);
            ImGui.InputInt("icon", ref value, 0);

            // Committed on release, not per keystroke: a half-typed "6123" is a different icon
            // and asking for one that does not exist throws out of the draw.
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                setIcon(Icons.Exists((uint)Math.Max(0, value)) ? (uint)value : fallback);
                config.Save();
            }
        }

        ImGui.PopID();
    }

    public override unsafe void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        var agent = AgentMap.Instance();
        var mapId = agent == null ? 0 : config.CurrentZoneOnly ? agent->CurrentMapId : agent->SelectedMapId;
        var here = mapId == 0 ? 0 : SpawnPoints.For(mapId).Count;

        ImGui.TextColored(here == 0 ? Bad : Dim, here == 0
            ? "No spawn points known for the map you are looking at."
            : $"{SpawnPoints.NameOf(mapId)}: {here} spawn points.");

        ImGui.TextColored(Dim, $"{SpawnPoints.Zones} zones in all, A Realm Reborn through Dawntrail.");

        ImGui.Separator();

        DrawRank("S rank", "S", () => config.ShowS, v => config.ShowS = v,
            () => config.SIcon, v => config.SIcon = v, Configuration.DefaultSIcon);
        DrawRank("A rank", "A", () => config.ShowA, v => config.ShowA = v,
            () => config.AIcon, v => config.AIcon = v, Configuration.DefaultAIcon);
        DrawRank("B rank", "B", () => config.ShowB, v => config.ShowB = v,
            () => config.BIcon, v => config.BIcon = v, Configuration.DefaultBIcon);

        ImGui.TextColored(Dim, "A point serving more than one rank is drawn once, as the rarest of them.");

        ImGui.Separator();

        var size = config.IconSize;
        if (ImGui.SliderFloat("Icon size", ref size, 12f, 64f, "%.0f"))
        {
            config.IconSize = Math.Clamp(size, 12f, 64f);
            config.Save();
        }

        var currentOnly = config.CurrentZoneOnly;
        if (ImGui.Checkbox("Only the zone I am standing in", ref currentOnly))
        {
            config.CurrentZoneOnly = currentOnly;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, the points are on whichever map you have open, so you can look at\n" +
                             "where you are about to go. On, they only appear for the zone you are in.");

        ImGui.Spacing();
        ImGui.TextWrapped("Spawn points are not in the game files - these are the ones the community " +
                          "mapped out, from Hunt Helper (MIT) and used with thanks.");
    }
}
