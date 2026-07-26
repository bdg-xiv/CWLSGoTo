using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace Laziness;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("Laziness###LazinessMain")
    {
        Size = new Vector2(340, 190);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public override void Draw()
    {
        var (poetics, shells, topsoil) = Plugin.Counts();
        ImGui.TextUnformatted($"Poetics: {poetics:N0}");
        ImGui.TextUnformatted($"Unidentifiable Shell: {shells:N0}");
        ImGui.TextUnformatted($"Grade 3 Shroud Topsoil: {topsoil:N0}");
        ImGui.Separator();

        var running = plugin.Running;
        ImGui.BeginDisabled(running);
        if (ImGui.Button("Buy soil", new Vector2(-1, 30)))
            plugin.StartBuySoil();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Stand near Hismena in Idyllshire.\n"
                + "Spends your poetics on Unidentifiable Shells, then trades\n"
                + "every shell to Bertana for Grade 3 Shroud Topsoil.");

        if (running && ImGui.Button("Stop", new Vector2(-1, 0)))
            plugin.Abort();

        ImGui.Separator();
        ImGui.TextWrapped(plugin.Status);
    }
}
