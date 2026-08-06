using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.Automation;
using ECommons.DalamudServices;
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

        Chore("Soil", "Stand near Hismena in Idyllshire.\n"
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

        // Second row, so the window doesn't grow into a long strip.
        Chore("Seals", "Stand near your Grand Company quartermaster.\n"
            + "Opens the Materials tab and spends every company seal on\n"
            + "whichever material the market pays best for per seal.",
            plugin.StartSeals);

        ImGui.SameLine();
        Chore("Desynth", "Stand near Ryubool Ja.\n"
            + "Opens the Quetzalli gear exchange (DoW, IL 720) and spends Sacks of\n"
            + "Nuts on every piece that would still raise a desynthesis level\n"
            + "(they're unique, so one of each), desynthesizes the lot, and repeats\n"
            + "until the nuts run out or nothing grants skill any more.",
            plugin.StartCrp);

        ImGui.EndDisabled();

        // Not a chore and not this plugin's business, but it belongs on whichever panel is
        // already open. Only offered when Glam Roulette is actually there to answer.
        if (Svc.Commands.Commands.ContainsKey("/glamroulette"))
        {
            ImGui.SameLine();
            Chore("Re-roll me", "Deals yourself another everything from Glam Roulette: outfit,\n"
                + "colours, shoes, the options on the mods it is built from, and your body.\n"
                + "Only does anything if you have it taking a turn on yourself.",
                () => Chat.ExecuteCommand("/glamroulette me"));
        }

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
