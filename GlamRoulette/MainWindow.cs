using System;
using System.Linq;
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
    private readonly PenumbraIpc penumbra;

    private string modFilter = string.Empty;
    private string clashFilter = string.Empty;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Configuration config, Wardrobe wardrobe, GlamourerIpc glamourer, Dyes dyes, PenumbraIpc penumbra)
        : base("Glam Roulette###GlamRoulette")
    {
        this.config = config;
        this.wardrobe = wardrobe;
        this.glamourer = glamourer;
        this.dyes = dyes;
        this.penumbra = penumbra;
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

    /// <summary>
    /// Mods that cannot be on together, so each person can be given only the one their outfit
    /// wants. Two outfits built on the same base item replace the same model file and a
    /// collection can only have one winner - but a player can.
    /// </summary>
    private void DrawExclusives()
    {
        if (!ImGui.TreeNode("Mods that clash with each other"))
            return;

        if (!penumbra.Available)
        {
            ImGui.TextColored(Bad, "Penumbra is not answering.");
            ImGui.TreePop();
            return;
        }

        ImGui.TextWrapped("Two outfit mods built on the same base item cannot both be on - they replace " +
                          "the same file, and one has to win. List them here and each person is given " +
                          "just the one their outfit needs, so both can be in the pool.");
        ImGui.TextWrapped("Several clashing pairs can share this list. Which ones are really in each " +
                          "other's way is worked out from the items they change, so a mod is only " +
                          "switched off for someone whose outfit needed the room.");

        foreach (var mod in config.ExclusiveMods.ToList())
        {
            ImGui.PushID("x" + mod.Directory);
            ImGui.Text($"  {mod.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                config.ExclusiveMods.Remove(mod);
                config.Save();
                wardrobe.RevertAll();
            }
            ImGui.PopID();
        }

        if (config.ExclusiveMods.Count == 1)
            ImGui.TextColored(Dim, "One on its own has nothing to clash with - add the other.");
        else if (config.ExclusiveMods.Count > 1)
            ImGui.TextColored(Dim, $"{wardrobe.ClashCount} of these have someone to fight.");

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##clashfilter", "Find a mod to add...", ref clashFilter, 100);

        if (clashFilter.Trim().Length >= 2)
        {
            var needle = clashFilter.Trim();
            var shown = 0;

            foreach (var (directory, name) in penumbra.Mods())
            {
                if (shown >= 8)
                    break;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (config.ExclusiveMods.Any(m => m.Directory == directory))
                    continue;

                shown++;
                if (!ImGui.Button($"{name}##clash{directory}"))
                    continue;

                config.ExclusiveMods.Add(new ModPick { Directory = directory, Name = name });
                config.Save();
                clashFilter = string.Empty;
                wardrobe.ForgetMods();
                break;
            }
        }

        ImGui.TreePop();
    }

    /// <summary>
    /// Which of a group's options are in play. A mod with forty variants is rarely forty
    /// variants you want to see, and a set of tick boxes is rarely all worth rolling.
    /// </summary>
    private void DrawGroupOptions(ModPick mod, string group, string[] options)
    {
        if (!ImGui.TreeNode($"which options##opts{group}"))
            return;

        var allowed = mod.Allowed(group);

        if (ImGui.SmallButton($"All##all{group}"))
        {
            mod.GroupOptions.Remove(group);
            config.Save();
            wardrobe.ForgetMods();
            allowed = null;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"None##none{group}"))
        {
            mod.GroupOptions[group] = [];
            config.Save();
            wardrobe.ForgetMods();
            allowed = mod.Allowed(group);
        }

        // Tall lists get their own scroll rather than pushing everything else off the window.
        var scrolled = options.Length > 8;
        if (scrolled)
            ImGui.BeginChild($"##list{group}", new Vector2(0, 150), true);

        foreach (var option in options)
        {
            var on = allowed?.Contains(option) ?? true;
            if (!ImGui.Checkbox($"{option}##{group}{option}", ref on))
                continue;

            // The absent-means-everything shorthand has to become a real set the moment one is
            // turned off, or there would be nothing to take it out of.
            var set = mod.Allowed(group) ?? [..options];
            if (on)
                set.Add(option);
            else
                set.Remove(option);

            mod.GroupOptions[group] = set;
            config.Save();
            wardrobe.ForgetMods();
        }

        if (scrolled)
            ImGui.EndChild();

        ImGui.TreePop();
    }

    /// <summary>
    /// The mods whose dropdowns get rolled, picked out one at a time. Deliberately not "every
    /// mod that is on": a size or body group has to match the wearer, and rolling one of those
    /// is how you get gaps instead of variety.
    /// </summary>
    private void DrawModOptions()
    {
        var on = config.RandomizeModOptions;
        if (ImGui.Checkbox("Randomise mod options", ref on))
        {
            config.RandomizeModOptions = on;
            config.Save();
            if (!on)
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rolls the dropdowns Penumbra shows on a mod - the material, the colour,\n" +
                             "which parts are on - so two people in the same mod are not in the same\n" +
                             "version of it. Set against the player rather than your collection, so\n" +
                             "nothing of yours is changed and it all comes off again.\n" +
                             "Each change costs that person a redraw, unlike a glamour.");

        var force = config.ForceRedraw;
        if (ImGui.Checkbox("Redraw people as soon as their mods change", ref force))
        {
            config.ForceRedraw = force;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A mod settings change only shows on a redraw, and a redraw is a character\n" +
                             "being unloaded and put back. That is what makes them pop, and it can\n" +
                             "disturb things on your own screen while it happens.\n" +
                             "Untick it and the settings are still set, but wait to be shown until the\n" +
                             "game reloads that person anyway - a zone change, a gearset, walking back\n" +
                             "into view. Nothing is ever made to flicker on your account.");

        if (config.ForceRedraw)
        {
            ImGui.Indent();
            var atOnce = config.RedrawAllAtOnce;
            if (ImGui.Checkbox("All at once rather than one a second", ref atOnce))
            {
                config.RedrawAllAtOnce = atOnce;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A crowd arriving together settles in one go instead of trickling in,\n" +
                                 "at the price of taking every redraw in the same frame - which is a\n" +
                                 "freeze as long as there are people to get through.\n" +
                                 "One a second is slower to look right but never stutters.");
            ImGui.Unindent();
        }

        if (!config.RandomizeModOptions)
            return;

        ImGui.Indent();

        if (!penumbra.Available)
        {
            ImGui.TextColored(Bad, "Penumbra is not answering - nothing can be rolled.");
            ImGui.Unindent();
            return;
        }

        foreach (var mod in config.RandomizedMods.ToList())
        {
            ImGui.PushID(mod.Directory);

            var groups = wardrobe.GroupsOf(mod.Directory);
            var rolled = groups.Count(g => !mod.SkipGroups.Contains(g.Key) && ModRoulette.Rollable(g.Value.Type));

            var open = ImGui.TreeNode($"{mod.Name}##node");
            ImGui.SameLine();
            ImGui.TextColored(rolled == 0 ? Bad : Dim, $"{rolled} group{(rolled == 1 ? "" : "s")} rolled");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                config.RandomizedMods.Remove(mod);
                config.Save();
                wardrobe.RevertAll();
            }

            if (open)
            {
                if (groups.Count == 0)
                    ImGui.TextColored(Dim, "No option groups - nothing here to roll.");

                foreach (var (group, (options, type)) in groups)
                {
                    if (!ModRoulette.Rollable(type))
                    {
                        ImGui.TextColored(Dim, $"{group} - an item's own attributes, kept as yours.");
                        continue;
                    }

                    var include = !mod.SkipGroups.Contains(group);
                    if (ImGui.Checkbox($"{group}##{group}", ref include))
                    {
                        if (include)
                            mod.SkipGroups.Remove(group);
                        else
                            mod.SkipGroups.Add(group);

                        config.Save();
                        wardrobe.ForgetMods();
                    }

                    var allowed = mod.Allowed(group);
                    var live = allowed == null ? options.Length : options.Count(allowed.Contains);

                    ImGui.SameLine();
                    ImGui.TextColored(Dim, type == PenumbraIpc.GroupType.Single
                        ? $"{live} of {options.Length}, one of them"
                        : $"{live} of {options.Length} toggles, any combination");

                    if (include)
                        DrawGroupOptions(mod, group, options);
                }

                ImGui.TextColored(Dim, "Unticked keeps whatever you have chosen in Penumbra.");

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##modfilter", "Find a mod to add...", ref modFilter, 100);

        if (modFilter.Trim().Length >= 2)
        {
            var needle = modFilter.Trim();
            var shown = 0;

            foreach (var (directory, name) in penumbra.Mods())
            {
                if (shown >= 8)
                    break;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (config.RandomizedMods.Any(m => m.Directory == directory))
                    continue;

                shown++;
                if (!ImGui.Button($"{name}##add{directory}"))
                    continue;

                config.RandomizedMods.Add(new ModPick { Directory = directory, Name = name });
                config.Save();
                modFilter = string.Empty;
                wardrobe.ForgetMods();
                break;
            }

            if (shown == 0)
                ImGui.TextColored(Dim, "Nothing matching that is left to add.");
        }

        ImGui.Unindent();
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

        var hroth = config.SwapHrothgarFemales;
        if (ImGui.Checkbox("Female Hrothgar turn up as Elezen", ref hroth))
        {
            config.SwapHrothgarFemales = hroth;
            config.Save();
            // Switching it on needs nothing - the next pass finds them. Switching it off has to
            // put them back, since nothing else is going to undo a race that is already applied.
            if (!hroth)
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Changes their clan on your screen only. Glamourer picks a face and hair\n" +
                             "that the new clan actually has - the numbers do not carry across races -\n" +
                             "so colouring and build follow them over but the face will not match.\n" +
                             "This redraws them once, and again if something puts them back.");

        if (config.SwapHrothgarFemales)
        {
            ImGui.Indent();
            var duskwight = config.HrothgarFemaleClan == 4;
            if (ImGui.RadioButton("Wildwood", !duskwight))
            {
                config.HrothgarFemaleClan = 3;
                config.Save();
                wardrobe.ForgetRaces();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Duskwight", duskwight))
            {
                config.HrothgarFemaleClan = 4;
                config.Save();
                wardrobe.ForgetRaces();
            }
            ImGui.Unindent();
        }

        var skipParty = config.SkipParty;
        if (ImGui.Checkbox("Leave party members alone", ref skipParty))
        {
            config.SkipParty = skipParty;
            config.Save();
        }

        var includeMe = config.IncludeMe;
        if (ImGui.Checkbox("Take a turn myself", ref includeMe))
        {
            config.IncludeMe = includeMe;
            config.Save();
            if (!includeMe)
                wardrobe.RevertMe();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Deals you an outfit too, from the same pool as everybody else and by\n" +
                             "the same rules. Still only on your own screen - but your look is the\n" +
                             "one Mare shares, so this is the one roll anybody else could see.\n" +
                             "Untick it to go straight back to your own glamour.");

        if (config.IncludeMe)
        {
            ImGui.Indent();
            var rotate = config.MyRotateMinutes;
            if (ImGui.SliderInt("My outfits last (minutes)", ref rotate, 0, 240))
            {
                config.MyRotateMinutes = Math.Max(0, rotate);
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Yours only - everyone else keeps theirs so they stay recognisable.\n" +
                                 "The clock runs per job, so your healer one going stale leaves your\n" +
                                 "tank one alone, and coming back to a job after a while is when you\n" +
                                 "find it has changed. It also changes under you if you stay on the\n" +
                                 "one job that long. The replacement is never the one just taken off.\n" +
                                 "Zero keeps yours as fixed as everybody else's, and an outfit you\n" +
                                 "chose to remember never goes stale.");
            ImGui.Unindent();
        }

        var automation = config.RestoreAutomation;
        if (ImGui.Checkbox("Put people back to their Glamourer automation", ref automation))
        {
            config.RestoreAutomation = automation;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What \"back to normal\" should mean. A plain revert undoes an automated\n" +
                             "design along with ours, which on yourself means losing your glamour\n" +
                             "rather than getting it back. Anyone with no automation is reverted\n" +
                             "the ordinary way regardless.");

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

        DrawModOptions();
        DrawExclusives();

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

        if (config.IncludeMe)
        {
            ImGui.SameLine();
            if (ImGui.Button("Re-roll me"))
                ECommons.DalamudServices.Svc.Chat.Print(wardrobe.RerollMe()
                    ? "[Glam Roulette] Dealing yourself another one."
                    : "[Glam Roulette] Nothing of yours to re-roll - it may be one you chose to keep.");
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
