using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace ModGuard.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 Orange = new(1f, 0.65f, 0f, 1f);
    private static readonly Vector4 Red = new(1f, 0.3f, 0.3f, 1f);

    private readonly Plugin plugin;
    private string newWatchTerm = string.Empty;

    public MainWindow(Plugin plugin) : base("Mod Guard###ModGuardMain")
    {
        Size = new Vector2(430, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;

        if (!plugin.PenumbraAvailable)
            ImGui.TextColored(Red, "Penumbra is not available!");

        if (plugin.ModsHidden)
            ImGui.TextColored(Orange, "Your Penumbra mods are currently HIDDEN.");
        else
            ImGui.TextColored(Green, "Your Penumbra mods are visible (normal).");

        if (plugin.ModsHidden)
        {
            if (ImGui.Button("Restore my mods now"))
                plugin.RestoreMods();
        }
        else
        {
            if (ImGui.Button("Hide my mods now"))
                plugin.HideMods(auto: false);
        }

        ImGui.Separator();

        var auto = config.AutoMode;
        if (ImGui.Checkbox("Automatically hide mods while a sync plugin is loaded", ref auto))
        {
            config.AutoMode = auto;
            config.Save();
        }
        ImGui.TextWrapped("Mods are restored automatically once no watched sync plugin is loaded anymore (only if they were hidden automatically - a manual hide stays until you restore it).");

        ImGui.Separator();

        ImGui.Text("Sync plugins currently loaded:");
        if (plugin.DetectedSyncPlugins.Count == 0)
        {
            ImGui.Text("  (none)");
        }
        else
        {
            foreach (var name in plugin.DetectedSyncPlugins)
                ImGui.TextColored(Orange, $"  {name}");
        }

        ImGui.Separator();

        ImGui.Text("Watched plugin name fragments:");
        for (var i = 0; i < config.WatchTerms.Count; i++)
        {
            ImGui.Text($"  {config.WatchTerms[i]}");
            ImGui.SameLine();
            if (ImGui.Button($"Remove##watchterm{i}"))
            {
                config.WatchTerms.RemoveAt(i);
                config.Save();
                break;
            }
        }

        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##newwatchterm", ref newWatchTerm, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(newWatchTerm))
        {
            config.WatchTerms.Add(newWatchTerm.Trim());
            config.Save();
            newWatchTerm = string.Empty;
        }
    }
}
