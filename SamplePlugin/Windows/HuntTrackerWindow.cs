using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;

namespace SamplePlugin.Windows;

// Modeled on Faloop Integration's active-mob window: one row per tracked hunt
// with teleport / open-map actions, except teleport runs the full Go To flow
// (world hop if needed, then aetheryte teleport, flag, and arrival actions).
public class HuntTrackerWindow : Window
{
    private readonly Plugin plugin;

    public HuntTrackerWindow(Plugin plugin) : base("CWLS Go To Hunts###CWLSGoToHunts")
    {
        Size = new Vector2(560, 240);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public override void Draw()
    {
        if (plugin.Hunts.Count == 0)
        {
            ImGui.TextWrapped("No active hunts. Watched messages with map links show up here, and entries disappear when a kill report for them arrives.");
            return;
        }

        if (ImGui.SmallButton("Clear all"))
        {
            plugin.Hunts.Clear();
            return;
        }

        if (!ImGui.BeginTable("##hunts", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Mob");
        ImGui.TableSetupColumn("World");
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Age");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        var removeIndex = -1;
        for (var i = 0; i < plugin.Hunts.Count; i++)
        {
            var hunt = plugin.Hunts[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(hunt.Label);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(hunt.WorldName.Length == 0 ? "-" : hunt.WorldName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{hunt.MapLink.PlaceName} ({hunt.MapLink.XCoord:0.0}, {hunt.MapLink.YCoord:0.0})");

            ImGui.TableNextColumn();
            var age = DateTime.UtcNow - hunt.AddedAt;
            ImGui.TextUnformatted(age.TotalHours >= 1 ? $"{(int)age.TotalHours}h{age.Minutes:00}m" : $"{age.Minutes:00}:{age.Seconds:00}");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Go To"))
                plugin.ExecuteGoTo(hunt.Aetheryte, hunt.World, hunt.MapLink);
            ImGui.SameLine();
            if (ImGui.SmallButton("Map"))
                Svc.GameGui.OpenMapWithMapLink(hunt.MapLink);
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
                removeIndex = i;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (removeIndex >= 0)
            plugin.Hunts.RemoveAt(removeIndex);
    }
}
