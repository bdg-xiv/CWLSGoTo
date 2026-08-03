using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GlamRoulette;

internal sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly Wardrobe wardrobe;
    private readonly GlamourerIpc glamourer;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Configuration config, Wardrobe wardrobe, GlamourerIpc glamourer)
        : base("Glam Roulette###GlamRoulette")
    {
        this.config = config;
        this.wardrobe = wardrobe;
        this.glamourer = glamourer;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 220),
            MaximumSize = new Vector2(700, 800),
        };
    }

    public override void Draw()
    {
        if (!glamourer.Available)
        {
            ImGui.TextColored(Bad, "Glamourer is not responding - nothing can be applied.");
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
            if (!enabled)
                wardrobe.RevertAll();
        }

        var pool = wardrobe.Pool();
        ImGui.TextColored(pool.Count == 0 ? Bad : Dim,
            $"{pool.Count} design{(pool.Count == 1 ? "" : "s")} in the pool, " +
            $"{wardrobe.Dressed} dressed right now, {wardrobe.Remembered} remembered.");

        if (pool.Count == 0)
            ImGui.TextColored(Bad, "Nothing to draw from - check the folder filter below.");

        ImGui.Separator();

        var folder = config.DesignFolder;
        if (ImGui.InputText("Design folder", ref folder, 200))
        {
            config.DesignFolder = folder;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only designs whose Glamourer folder path starts with this are used.\n" +
                             "Leave it empty to draw from every design you have.");

        var femaleOnly = config.FemaleOnly;
        if (ImGui.Checkbox("Female characters only", ref femaleOnly))
        {
            config.FemaleOnly = femaleOnly;
            config.Save();
            if (!femaleOnly)
                wardrobe.RevertAll();
        }

        var skipParty = config.SkipParty;
        if (ImGui.Checkbox("Leave party members alone", ref skipParty))
        {
            config.SkipParty = skipParty;
            config.Save();
        }

        var dyes = config.RandomizeDyes;
        if (ImGui.Checkbox("Randomise dyes", ref dyes))
        {
            config.RandomizeDyes = dyes;
            config.Save();
            wardrobe.RerollEverybody();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-dyes each slot after the outfit goes on, so two people wearing\n" +
                             "the same design still look different. The colours are derived from\n" +
                             "who is wearing it, so they stay put rather than shimmering.");

        if (config.RandomizeDyes)
        {
            var second = config.DyeSecondChannel;
            if (ImGui.Checkbox("Roll the second dye channel separately", ref second))
            {
                config.DyeSecondChannel = second;
                config.Save();
                wardrobe.RerollEverybody();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off means both channels get the same colour, which is tamer.");
        }

        var reapply = config.Reapply;
        if (ImGui.Checkbox("Re-apply periodically", ref reapply))
        {
            config.Reapply = reapply;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Anything that redraws a character drops the design and Glamourer\n" +
                             "will not put it back, so this quietly nails it on again.");

        if (config.Reapply)
        {
            var seconds = config.ReapplySeconds;
            if (ImGui.SliderInt("Every (seconds)", ref seconds, 5, 300))
            {
                config.ReapplySeconds = Math.Max(5, seconds);
                config.Save();
            }
        }

        ImGui.Separator();

        if (ImGui.Button("Re-roll everyone"))
        {
            var count = wardrobe.RerollEverybody();
            ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Re-rolling {count} remembered outfit(s).");
        }
        ImGui.SameLine();
        if (ImGui.Button("Put everyone back"))
            wardrobe.RevertAll();

        ImGui.Spacing();
        ImGui.TextColored(Dim, "Right-click a player's name for \"Re-roll outfit\" to re-roll just them.");
        ImGui.TextWrapped("This only changes how other people look on your screen. They see themselves " +
                          "normally, and so does everyone else.");
    }
}
