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
    private readonly Dyes dyes;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Configuration config, Wardrobe wardrobe, GlamourerIpc glamourer, Dyes dyes)
        : base("Glam Roulette###GlamRoulette")
    {
        this.config = config;
        this.wardrobe = wardrobe;
        this.glamourer = glamourer;
        this.dyes = dyes;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 220),
            MaximumSize = new Vector2(700, 800),
        };
    }

    /// <summary>One tier's weight, with the share of rolls it actually works out to - the
    /// number that matters is not the weight but what it does once tier sizes are in play.</summary>
    private void DrawWeight(string label, Dyes.Tier tier, Func<int> get, Action<int> set)
    {
        var value = get();
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderInt(label, ref value, 0, 50))
        {
            set(Math.Max(0, value));
            config.Save();
            wardrobe.RerollEverybody();
        }

        ImGui.SameLine();
        ImGui.TextColored(Dim, $"{dyes.Share(tier):P0} of rolls, {dyes.Count(tier)} dyes");
    }

    /// <summary>A discipline's subfolder, with a live count so a typo is obvious.</summary>
    private void DrawFolder(string label, Func<string> get, Action<string> set, JobPools.Group group)
    {
        var value = get();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputText(label, ref value, 100))
        {
            set(value);
            config.Save();
            wardrobe.RerollEverybody();
        }

        ImGui.SameLine();
        var count = wardrobe.PoolFor(group).Count;
        ImGui.TextColored(count == 0 ? Bad : Dim, $"{count} design{(count == 1 ? "" : "s")}");
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
            $"{wardrobe.Dressed} dressed right now, {wardrobe.Remembered} remembered" +
            (wardrobe.Kept > 0 ? $" ({wardrobe.Kept} kept)." : "."));

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

        var byJob = config.MatchJobCategory;
        if (ImGui.Checkbox("Separate pools per role", ref byJob))
        {
            config.MatchJobCategory = byJob;
            config.Save();
            wardrobe.RerollEverybody();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draws from a subfolder of the design folder chosen by what the wearer is.\n" +
                             "A role folder is used if it has anything in it; failing that the coarser\n" +
                             "war or magic folder; failing that the whole pool. Both \"tank\" and\n" +
                             "\"war/tank\" work, so organise it flat or nested as you like.");

        if (config.MatchJobCategory)
        {
            ImGui.Indent();
            DrawFolder("Tanks", () => config.TankFolder, v => config.TankFolder = v, JobPools.Group.Tank);
            DrawFolder("Melee DPS", () => config.MeleeFolder, v => config.MeleeFolder = v, JobPools.Group.Melee);
            DrawFolder("Physical ranged", () => config.RangedFolder, v => config.RangedFolder = v, JobPools.Group.Ranged);
            DrawFolder("Magical ranged", () => config.CasterFolder, v => config.CasterFolder = v, JobPools.Group.Caster);
            DrawFolder("Healers", () => config.HealerFolder, v => config.HealerFolder = v, JobPools.Group.Healer);
            DrawFolder("Crafters", () => config.CrafterFolder, v => config.CrafterFolder = v, JobPools.Group.Crafter);
            DrawFolder("Gatherers", () => config.GathererFolder, v => config.GathererFolder = v, JobPools.Group.Gatherer);

            ImGui.Spacing();
            ImGui.TextDisabled("Fallbacks, used when a role has no folder of its own:");
            var war = config.WarFolder;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.InputText("War", ref war, 100))
            {
                config.WarFolder = war;
                config.Save();
                wardrobe.RerollEverybody();
            }

            var magic = config.MagicFolder;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.InputText("Magic", ref magic, 100))
            {
                config.MagicFolder = magic;
                config.Save();
                wardrobe.RerollEverybody();
            }

            ImGui.Spacing();
            var shared = config.IncludeSharedDesigns;
            if (ImGui.Checkbox("Anything loose in the design folder suits everyone", ref shared))
            {
                config.IncludeSharedDesigns = shared;
                config.Save();
                wardrobe.RerollEverybody();
            }
            ImGui.Unindent();
        }

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
            ImGui.SetTooltip("Re-dyes the outfit after it goes on, so two people wearing the same\n" +
                             "design still look different. One colour per channel across the whole\n" +
                             "outfit, not per slot. The colours are derived from who is wearing it,\n" +
                             "so they stay put rather than shimmering.");

        if (config.RandomizeDyes)
        {
            ImGui.Indent();
            DrawWeight("Metallic", Dyes.Tier.Metallic, () => config.MetallicWeight, v => config.MetallicWeight = v);
            DrawWeight("Premium", Dyes.Tier.Premium, () => config.PremiumWeight, v => config.PremiumWeight = v);
            DrawWeight("Standard", Dyes.Tier.Standard, () => config.StandardWeight, v => config.StandardWeight = v);
            ImGui.TextDisabled("Premium is the 668-gil tier: the pastels, the darks, Pure White and Jet Black.");
            ImGui.Unindent();

            var second = config.DyeSecondChannel;
            if (ImGui.Checkbox("Roll the second dye channel separately", ref second))
            {
                config.DyeSecondChannel = second;
                config.Save();
                wardrobe.RerollEverybody();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The two channels are rolled independently and can land on the same\n" +
                                 "colour by chance. Off ties them together so that always happens.");
        }

        var remember = config.RememberMinutes;
        if (ImGui.SliderInt("Forget after (minutes)", ref remember, 0, 240))
        {
            config.RememberMinutes = Math.Max(0, remember);
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A player who has not been seen for this long loses their outfit and\n" +
                             "gets a new one next time. Zero keeps everyone forever, which is what\n" +
                             "this used to do - and why there were hundreds of them.\n" +
                             "Right-click someone and choose \"Remember this outfit\" to keep the one\n" +
                             "they are wearing. That is per outfit, so their other roles still age out.");

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
