using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using System;
using System.Numerics;

namespace ModSnap.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 Red = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 Grey = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("Mod Snap###ModSnapMain")
    {
        Size = new Vector2(460, 340);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;

        if (plugin.CheckPenumbraAvailable())
            ImGui.TextColored(Green, "Penumbra is available.");
        else
            ImGui.TextColored(Red, "Penumbra is not available!");

        ImGui.TextWrapped("Snapshots save everything currently applied to a character (including synced mods) " +
                          "as a Penumbra Character Pack and install it right away: the mod and a collection land in Penumbra " +
                          "under \"PCP\", and Glamourer adds a matching design to its \"PCP\" folder.");
        ImGui.TextColored(Grey, "The design requires Glamourer's \"Attach to PCP\" setting (enabled by default).");

        ImGui.Separator();

        var contextMenu = config.ContextMenuEnabled;
        if (ImGui.Checkbox("Add \"Save Mods + Appearance\" to character right-click menus", ref contextMenu))
        {
            config.ContextMenuEnabled = contextMenu;
            config.Save();
        }

        var playersOnly = config.PlayersOnly;
        if (ImGui.Checkbox("Only on players (not NPCs)", ref playersOnly))
        {
            config.PlayersOnly = playersOnly;
            config.Save();
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("Label (optional, timestamp if empty)", ref plugin.NextLabel, 64);

        var target = Svc.Targets.Target as ICharacter;
        ImGui.BeginDisabled(plugin.SnapshotInProgress || target == null);
        if (ImGui.Button(target != null ? $"Snapshot target: {target.Name.TextValue}" : "Snapshot target (none)") && target != null)
            plugin.SnapshotCharacter(target.ObjectIndex, target.Name.TextValue);
        ImGui.EndDisabled();

        ImGui.SameLine();
        var self = Svc.Objects.LocalPlayer;
        ImGui.BeginDisabled(plugin.SnapshotInProgress || self == null);
        if (ImGui.Button("Snapshot yourself") && self != null)
            plugin.SnapshotCharacter(self.ObjectIndex, self.Name.TextValue);
        ImGui.EndDisabled();

        if (plugin.SnapshotInProgress)
            ImGui.TextColored(Grey, "Snapshot in progress...");

        if (plugin.LastResult != null)
        {
            ImGui.Separator();
            ImGui.TextWrapped($"Last result: {plugin.LastResult}");
            if (plugin.LastPcpFile != null)
                ImGui.TextWrapped($"Pack file: {plugin.LastPcpFile}");
        }
    }
}
