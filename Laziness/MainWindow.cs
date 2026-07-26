using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;

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
        // One row of chore buttons; more get added beside these.
        ImGui.BeginDisabled(plugin.Running);

        Chore("Buy soil", "Stand near Hismena in Idyllshire.\n"
            + "Spends your poetics on Unidentifiable Shells, then trades\n"
            + "every shell to Bertana for Grade 3 Shroud Topsoil.",
            plugin.StartBuySoil);

        ImGui.SameLine();
        Chore("Allied", "Stand near a hunt billmaster.\n"
            + "Spends every Allied Seal on ventures or aetheryte tickets,\n"
            + "whichever you hold fewer of right now.",
            () => plugin.StartSealExchange(centurio: false));

        ImGui.SameLine();
        Chore("Centurio", "Stand near Ardolain in Ishgard.\n"
            + "Spends every Centurio Seal on ventures or aetheryte tickets,\n"
            + "whichever you hold fewer of right now.",
            () => plugin.StartSealExchange(centurio: true));

        ImGui.SameLine();
        Chore("Maths", "Stand near Zircon in Solution Nine.\n"
            + "Checks current market prices for the six tradeable wares and\n"
            + "spends your Mathematics tomestones on whichever pays best.",
            plugin.StartMaths);

        ImGui.EndDisabled();

        if (!plugin.Running)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            plugin.Abort();
    }

    private static void Chore(string label, string tooltip, Action onClick)
    {
        if (ImGui.Button(label))
            onClick();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }
}
