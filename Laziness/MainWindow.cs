using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Laziness;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public MainWindow(Plugin plugin, Configuration configuration)
        : base("Laziness###LazinessMain", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.configuration = configuration;
    }

    public override void OnOpen() => RememberOpenState(true);

    public override void OnClose() => RememberOpenState(false);

    private void RememberOpenState(bool open)
    {
        if (configuration.WindowOpen == open)
            return;

        configuration.WindowOpen = open;
        configuration.Save();
    }

    public override void Draw()
    {
        // One row of chore buttons; more get added beside this one.
        ImGui.BeginDisabled(plugin.Running);
        if (ImGui.Button("Buy soil"))
            plugin.StartBuySoil();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Stand near Hismena in Idyllshire.\n"
                + "Spends your poetics on Unidentifiable Shells, then trades\n"
                + "every shell to Bertana for Grade 3 Shroud Topsoil.");

        if (!plugin.Running)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            plugin.Abort();
    }
}
