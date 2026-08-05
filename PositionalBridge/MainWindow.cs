using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PositionalBridge;

internal sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly Bridge bridge;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.45f, 1f, 0.5f, 1f);

    public MainWindow(Configuration config, Bridge bridge)
        : base("Positional Bridge###PositionalBridge")
    {
        this.config = config;
        this.bridge = bridge;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 220),
            MaximumSize = new Vector2(700, 600),
        };
    }

    public override void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
            if (!enabled)
                bridge.Reset();
        }

        if (Bridge.RotationSolverPresent)
            ImGui.TextColored(Bad, "Rotation Solver is installed too - you will have both talking\n" +
                                   "to Avarice at once, and whichever spoke last wins.");

        ImGui.Separator();

        if (!bridge.JobSupported)
        {
            ImGui.TextColored(Dim, "Nothing to do on this job. Viper is the only one wired up so far.");
        }
        else
        {
            ImGui.Text($"Watching: {Bridge.NameOf(bridge.Root)}");
            ImGui.Text($"Wrath says: {Bridge.NameOf(bridge.Resolved)}");
            ImGui.TextColored(bridge.Sent == 0 ? Dim : Good, $"Told Avarice: {Bridge.NameOf(bridge.Sent)}");
        }

        ImGui.Separator();
        ImGui.TextWrapped("Avarice will not use this until you tick \"Use Rotation Solver to anticipate " +
                          "positionals\" in its settings, under the anticipation options. That box only " +
                          "appears once it has heard from something, so stand on a Viper for a moment " +
                          "and it will be there.");

        ImGui.Spacing();
        ImGui.TextColored(Dim, "Wrath has no \"what will I cast next\" of its own. This asks the game what\n" +
                               "your combo button currently is, which Wrath has already answered by\n" +
                               "hooking that very lookup - so it is what will fire, not a guess at it.");
    }
}
