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
    private readonly CustomizePlusIpc cplus;
    private readonly Shoes shoes;

    private string modFilter = string.Empty;
    private string clashFilter = string.Empty;

    private System.Collections.Generic.IReadOnlyList<(Guid Id, string Name, string Path)>? profiles;
    private DateTime profilesAt = DateTime.MinValue;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Configuration config, Wardrobe wardrobe, GlamourerIpc glamourer, Dyes dyes,
        PenumbraIpc penumbra, CustomizePlusIpc cplus, Shoes shoes)
        : base("Glam Roulette###GlamRoulette")
    {
        this.config = config;
        this.wardrobe = wardrobe;
        this.glamourer = glamourer;
        this.dyes = dyes;
        this.shoes = shoes;
        this.penumbra = penumbra;
        this.cplus = cplus;
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
    /// One Customize+ profile of yours handed to everybody, with the chest rolled per person.
    /// This one costs nothing to apply - Customize+ works on the bones every frame rather than
    /// baking anything into a model, and its temporary profiles are filed against the character
    /// rather than an object, so they survive a zone change on their own.
    /// </summary>
    private void DrawShapes()
    {
        // Above the Customize+ half deliberately: this is the mesh everybody is built from, and
        // the bone scaling that follows is measured from whatever it finds.
        var bust = config.MaxBust;
        if (ImGui.Checkbox("Everyone at a full bust", ref bust))
        {
            config.MaxBust = bust;
            config.Save();
            // Off has to put them back - a customization already applied is not undone by
            // ceasing to ask for it - and on wants asking for everybody afresh.
            if (bust)
                wardrobe.ForgetRaces();
            else
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts the game's own bust slider at the top for every woman, on your screen\n" +
                             "only, before anything else touches them. That is a different thing from the\n" +
                             "body shape below: this changes the mesh the body is built from, and the\n" +
                             "bones are then scaled on top of whatever they find - so the two stack, and\n" +
                             "this is the floor the roll is measured from rather than a rival to it.\n" +
                             "Costs one redraw per person, unlike the bones, which cost none.\n" +
                             "Applies to men who are being turned into women, and to nobody else.");

        var on = config.RandomizeShapes;
        if (ImGui.Checkbox("Randomise body shape", ref on))
        {
            config.RandomizeShapes = on;
            config.Save();
            if (on)
                wardrobe.ForgetShapes();
            else
                wardrobe.RevertShapes();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Gives everybody the bones of one of your Customize+ profiles, with the\n" +
                             "chest rolled per person so they are not all the same shape. Follows the\n" +
                             "player rather than the outfit - what job somebody is on has no business\n" +
                             "changing their body - and the size is derived from who they are, so it\n" +
                             "stays put. Costs no redraw at all.\n" +
                             "It takes the place of any Customize+ profile that person would otherwise\n" +
                             "have had, yours for them included, and comes off again the moment this\n" +
                             "is unticked.");

        if (!config.RandomizeShapes)
            return;

        ImGui.Indent();

        if (!cplus.Available)
        {
            ImGui.TextColored(Bad, "Customize+ is not answering, or its render hook did not take.");
            ImGui.Unindent();
            return;
        }

        // Held for a moment, since this is drawn every frame the window is open and reading the
        // list is a round trip for something that changes when you make a profile.
        if (profiles == null || DateTime.UtcNow - profilesAt > TimeSpan.FromSeconds(2))
        {
            profiles = cplus.Profiles();
            profilesAt = DateTime.UtcNow;
        }

        var chosen = profiles.FirstOrDefault(p => p.Id == config.ShapeProfile);
        var label = chosen.Id != Guid.Empty
            ? chosen.Name
            : config.ShapeProfile == Guid.Empty
                ? "Pick one..."
                : $"{config.ShapeProfileName} (gone from Customize+)";

        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Profile", label))
        {
            foreach (var (id, name, path) in profiles)
            {
                if (ImGui.Selectable(name, id == config.ShapeProfile))
                {
                    config.ShapeProfile = id;
                    config.ShapeProfileName = name;
                    config.Save();
                    wardrobe.ForgetShapes();
                }

                if (ImGui.IsItemHovered() && path.Length > 0 && path != name)
                    ImGui.SetTooltip(path);
            }

            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Read, never touched. Leave it switched off in Customize+ if you like -\n" +
                             "we take a copy of its bones rather than turning the profile on.");

        if (config.ShapeProfile == Guid.Empty)
        {
            ImGui.TextColored(Dim, "Nothing is applied until a profile is chosen.");
            ImGui.Unindent();
            return;
        }

        // Written as it is dragged, so it can be seen happening, but only saved once it is let
        // go - the size is part of what each person was given, so the next pass swaps everybody
        // over on its own without anything having to be taken off.
        var min = config.ShapeMin;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat("Smallest", ref min, 0.5f, 4f, "%.2fx"))
            config.ShapeMin = min;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        var max = config.ShapeMax;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat("Biggest", ref max, 0.5f, 4f, "%.2fx"))
            config.ShapeMax = max;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Multiples of the vanilla size, not of the profile's. One is untouched.\n" +
                             "Both sides match - a size is rolled once per person and both bones get it.\n" +
                             "If the profile shapes the chest rather than merely enlarging it, that\n" +
                             "shape is kept and only scaled, so what is rolled is size and not form.");

        var bones = wardrobe.RolledBones;
        ImGui.TextColored(Dim, bones.Count > 0
            ? $"Rolling {string.Join(", ", bones.Select(Shapes.NameOf))} - {wardrobe.Shaped} shaped right now."
            : "That profile leaves the chest alone, so all four chest bones are set on top of it - " +
              $"{wardrobe.Shaped} shaped right now.");

        ImGui.Unindent();
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
    private void DrawGroupOptions(ModPick mod, string group, string[] options, PenumbraIpc.GroupType type)
    {
        if (!ImGui.TreeNode($"which options##opts{group}"))
            return;

        // What unticking actually does is not the same for the two kinds, and the difference
        // matters: on a dropdown it takes an option out of the running, on tick boxes it hands
        // that one option back to you. Said here rather than once at the bottom, where it could
        // only ever be true of one of them.
        ImGui.TextColored(Dim, type == PenumbraIpc.GroupType.Single
            ? "One of the ticked is picked. Unticked are never picked."
            : "Each ticked one is flipped by itself. Unticked keep what you set in Penumbra.");

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
            var changed = ImGui.Checkbox($"{option}##{group}{option}", ref on);

            // Options that are only a pointer at another mod's files. Said here rather than left
            // to be found out by picking one and seeing nothing happen.
            if (ModRoulette.Requirement(option) is { } asked)
            {
                var companion = wardrobe.CompanionOf(option);
                ImGui.SameLine();
                ImGui.TextColored(companion == null ? Bad : Dim,
                    companion == null ? "not installed" : "+ rolled too");

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(companion is { } found
                        ? $"Needs {found.Name}, which shows nothing until it is on.\n" +
                          "Whoever draws this option gets that mod switched on and its own\n" +
                          "options rolled with it, so the effect varies person to person."
                        : $"Wants \"{asked}\", and nothing installed matches that name.\n" +
                          "Drawing this option would show nothing at all.");
            }

            if (!changed)
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
    /// How often each outfit comes up against the others in its pool, listed whole and split by
    /// the folder it sits in - which is the pool it is actually drawn from, so the grouping is
    /// the thing that decides the odds rather than a tidy way of showing them.
    /// </summary>
    private void DrawDesignOdds()
    {
        if (!ImGui.TreeNode("Odds##designodds"))
            return;

        ImGui.TextColored(Dim, "Two is twice as often as a one, zero is never dealt.");

        var root = config.DesignFolder.Trim().Trim('/');
        var folders = wardrobe.Pool()
            .GroupBy(d => FolderOf(d.Path, root))
            .OrderBy(g => g.Key.Length == 0 ? string.Empty : g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folders.Count == 0)
            ImGui.TextColored(Bad, "No designs in that folder.");

        foreach (var folder in folders)
        {
            var loose = folder.Key.Length == 0;
            var total = folder.Sum(d => (long)wardrobe.WeightOf(d.Id));

            if (!ImGui.TreeNode($"{(loose ? "shared with everyone" : folder.Key)}##f{folder.Key}"))
                continue;

            ImGui.TextColored(Dim, $"{folder.Count()} outfit(s), {total} share(s) between them");

            foreach (var (id, name, _) in folder.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.PushID(id.ToString());

                // Right beside the number, because deciding how often you want to see an outfit
                // means seeing it, and going to find it elsewhere to do that is how you guess.
                if (ImGui.SmallButton("Wear"))
                    WearNow(id, name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Deals it to you now, rolled as anybody would get it.");

                ImGui.SameLine();
                var weight = wardrobe.WeightOf(id);
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##weight", ref weight))
                {
                    config.DesignWeights[id] = Math.Max(0, weight);
                    config.Save();
                    wardrobe.ForgetPool();
                }

                // Against its own folder rather than the whole pool - that is the draw it is
                // really in, and a number that is true is worth more than one that is tidy.
                ImGui.SameLine();
                ImGui.TextColored(Dim, total > 0 ? $"{wardrobe.WeightOf(id) / (float)total:P1}" : "never");

                ImGui.SameLine();
                var bare = config.LeaveChestDesigns.Contains(id);
                if (ImGui.Checkbox("##barechest", ref bare))
                {
                    if (bare)
                        config.LeaveChestDesigns.Add(id);
                    else
                        config.LeaveChestDesigns.Remove(id);
                    config.Save();
                    wardrobe.ForgetChestFlags();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This outfit sizes its own chest: whoever wears it gets no max bust\n" +
                                     "and no rolled shape, so the mesh is the only thing doing the sizing.");

                ImGui.SameLine();
                ImGui.TextUnformatted(name);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    /// <summary>
    /// Puts a pair of shoes on yourself until you stop, or takes them off again. Needs you to be
    /// taking a turn for the same reason an outfit does - the pass is what puts them on, and it
    /// passes you over otherwise.
    /// </summary>
    private void TryShoes(uint? item)
    {
        if (item != null && !config.IncludeMe)
        {
            config.IncludeMe = true;
            config.Save();
            ECommons.DalamudServices.Svc.Chat.Print(
                "[Glam Roulette] Taking a turn yourself, since you asked to try a pair on.");
        }

        wardrobe.TryShoes(item);
        ECommons.DalamudServices.Svc.Chat.Print(item is { } worn
            ? $"[Glam Roulette] Trying {shoes.NameOf(worn)} on you."
            : "[Glam Roulette] Back to the pair you were dealt.");
    }

    /// <summary>Puts one on yourself, taking a turn if you were not.</summary>
    private void WearNow(Guid design, string name)
    {
        if (!config.IncludeMe)
        {
            config.IncludeMe = true;
            ECommons.DalamudServices.Svc.Chat.Print(
                "[Glam Roulette] Taking a turn yourself, since you asked for an outfit.");
        }

        if (wardrobe.WearMyself(design))
            ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Putting {name} on you.");
    }

    /// <summary>The folder a design sits in, below the design folder. Empty for one sitting
    /// loose in the root, which is the pool everybody shares.</summary>
    private static string FolderOf(string path, string root)
    {
        var rest = root.Length > 0 && path.Length > root.Length ? path[(root.Length + 1)..] : path;
        var cut = rest.LastIndexOf('/');
        return cut < 0 ? string.Empty : rest[..cut];
    }

    /// <summary>
    /// How often each dye comes up, listed whole and split by tier. A dye given a number of its
    /// own answers for itself and its tier stops speaking for it, which is the only way to say
    /// "that one twice as often" while a tier is otherwise the smallest thing there is.
    /// </summary>
    private void DrawDyeOdds()
    {
        if (!ImGui.TreeNode("Particular dyes##dyeodds"))
            return;

        ImGui.TextColored(Dim, "A dye given a number here answers for itself instead of its tier.");

        var stains = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Stain>();

        foreach (var tier in new[] { Dyes.Tier.Metallic, Dyes.Tier.Premium, Dyes.Tier.Standard })
        {
            var inTier = dyes.All.Where(d => d.Tier == tier)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (inTier.Count == 0)
                continue;

            if (!ImGui.TreeNode($"{tier}##t{tier}"))
                continue;

            ImGui.TextColored(Dim, $"{inTier.Count} dye(s), {dyes.Share(tier):P0} of rolls between them");

            foreach (var (id, name, _) in inTier)
            {
                ImGui.PushID(id);

                // The name of a dye is not its colour, and choosing colours off a list of names
                // is guesswork - so the colour the game has for it goes beside the name.
                if (stains.GetRowOrDefault(id) is { } stain)
                {
                    var packed = stain.Color;
                    ImGui.ColorButton($"##swatch{id}", new Vector4(
                            (packed >> 16 & 0xFF) / 255f, (packed >> 8 & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(16, 16));
                    ImGui.SameLine();
                }

                // No re-roll: a colour is worked out from the same sum every pass, so changing
                // what the sum weighs changes the colour on its own. Throwing away everybody's
                // outfit because a number was typed into a dye box would be a strange price.
                var weight = dyes.WeightOf(id);
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##dyeweight", ref weight))
                {
                    config.DyeWeights[id] = Math.Max(0, weight);
                    config.Save();
                }

                ImGui.SameLine();
                if (dyes.IsNamed(id))
                {
                    if (ImGui.SmallButton("Tier"))
                    {
                        config.DyeWeights.Remove(id);
                        config.Save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Hands it back to its tier.");

                    ImGui.SameLine();
                }

                ImGui.TextColored(Dim, $"{dyes.ShareOf(id):P2}");
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private string tryOnFilter = string.Empty;

    /// <summary>
    /// Puts a named outfit on yourself instead of waiting for it to come up. Everything below the
    /// outfit is still dealt as usual - the colours, the shoes, the options on the mods it is
    /// built from - so what you get is one of them as somebody would actually be dealt it, which
    /// is the only way to see whether it holds together. Picking the one you have on already
    /// deals it again with a fresh set of options.
    /// </summary>
    private void DrawTryOn()
    {
        var pool = wardrobe.Pool();
        if (pool.Count == 0)
            return;

        var worn = wardrobe.MyDesign;
        var current = worn is { } design && pool.FirstOrDefault(d => d.Id == design) is { Name.Length: > 0 } mine
            ? mine.Path
            : "nothing of mine";

        ImGui.SetNextItemWidth(260f);
        if (ImGui.BeginCombo("Try one on##tryon", current))
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##tryonfilter", "Find...", ref tryOnFilter, 100);

            var needle = tryOnFilter.Trim();
            foreach (var (id, _, path) in pool
                         .Where(d => needle.Length == 0
                                     || d.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                         .OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!ImGui.Selectable($"{path}##try{id}", id == worn))
                    continue;

                // Nothing to put it on otherwise - the pass skips you entirely - so this is
                // taken as asking for that as well rather than quietly doing nothing.
                if (!config.IncludeMe)
                {
                    config.IncludeMe = true;
                    ECommons.DalamudServices.Svc.Chat.Print(
                        "[Glam Roulette] Taking a turn yourself, since you asked for an outfit.");
                }

                if (wardrobe.WearMyself(id))
                    ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Putting {path} on you.");

                tryOnFilter = string.Empty;
                break;
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Deals you one you name rather than one drawn for you. The colours, the\n" +
                             "shoes and the options on its mods are all still rolled, so it arrives\n" +
                             "the way somebody would actually be dealt it.\n" +
                             "Picking the one you have on already deals it again, rolled afresh.");
    }

    /// <summary>
    /// The mods whose dropdowns get rolled, picked out one at a time. Deliberately not "every
    /// mod that is on": a size or body group has to match the wearer, and rolling one of those
    /// is how you get gaps instead of variety.
    /// </summary>
    private string shoeFilter = string.Empty;
    private string shoeDesignFilter = string.Empty;

    /// <summary>
    /// A pool of shoes to deal out in place of the ones a design specifies, and the designs
    /// that want it. Per design rather than for all of them, because on most outfits the shoes
    /// are part of the outfit - it is only the ones built around a bare leg where a pair is
    /// worth varying, and varying them there beats keeping a near-identical design per pair.
    /// </summary>
    private void DrawShoes()
    {
        var on = config.RollShoes;
        if (ImGui.Checkbox("Deal out shoes", ref on))
        {
            config.RollShoes = on;
            config.Save();
            wardrobe.ForgetPool();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Replaces the shoes on the designs you name below with a pair from\n" +
                             "the pool, one per person, the same way their dyes are decided.");

        if (!config.RollShoes)
            return;

        ImGui.Indent();

        ImGui.TextColored(Dim, config.ShoePool.Count == 0
            ? "No shoes in the pool yet - nothing will change."
            : $"{config.ShoePool.Count} pair(s) in the pool, on {config.RollShoesFor.Count} design(s).");

        ImGui.TextColored(Dim, "Two is twice as often as a one, zero is never dealt.");

        foreach (var item in config.ShoePool.ToList())
        {
            ImGui.PushID((int)item);

            if (ImGui.SmallButton("x"))
            {
                config.ShoePool.Remove(item);
                config.ShoeWeights.Remove(item);
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes this pair out of the pool.");

            // Straight onto your own feet, whatever your outfit was dealt and whether or not it
            // is one whose shoes are dealt at all - deciding how often you want a pair means
            // seeing it, and most of the outfits worth seeing it on are not on the list yet.
            ImGui.SameLine();
            var worn = shoes.TryingOn == item;
            if (ImGui.SmallButton(worn ? "Stop" : "Wear"))
            {
                TryShoes(worn ? null : item);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(worn
                    ? "Puts your dealt pair back."
                    : "Puts this pair on you until you stop, over whatever you are wearing.");

            ImGui.SameLine();
            var weight = shoes.WeightOf(item);
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt("##shoeweight", ref weight))
                config.ShoeWeights[item] = Math.Max(0, weight);

            // Held until the box is let go: the pool is part of what everybody was dealt, so a
            // change re-deals it, and paying that per keystroke is a crowd changing shoes twice
            // over for one edit.
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.Save();
                wardrobe.ForgetPool();
            }

            ImGui.SameLine();
            ImGui.TextColored(Dim, $"{shoes.ShareOf(item):P1}");
            ImGui.SameLine();
            ImGui.TextUnformatted(shoes.NameOf(item));
            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##shoefilter", "Find shoes to add...", ref shoeFilter, 100);

        if (shoeFilter.Trim().Length >= 2)
        {
            var needle = shoeFilter.Trim();
            var shown = 0;

            foreach (var (id, name) in shoes.Catalogue())
            {
                if (shown >= 8)
                    break;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 || config.ShoePool.Contains(id))
                    continue;

                shown++;
                if (!ImGui.Button($"{name}##addshoe{id}"))
                    continue;

                config.ShoePool.Add(id);
                config.Save();
                shoeFilter = string.Empty;
                break;
            }

            if (shown == 0)
                ImGui.TextColored(Dim, "Nothing matching that is left to add.");
        }

        // The designs that want it, named the same way mods are.
        foreach (var design in config.RollShoesFor.ToList())
        {
            if (ImGui.SmallButton($"x##shoedesign{design}"))
            {
                config.RollShoesFor.Remove(design);
                config.Save();
            }

            ImGui.SameLine();
            var known = wardrobe.Pool().FirstOrDefault(d => d.Id == design);
            ImGui.TextUnformatted(known.Name ?? design.ToString());
        }

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##shoedesignfilter", "Find a design to add...", ref shoeDesignFilter, 100);

        if (shoeDesignFilter.Trim().Length >= 2)
        {
            var needle = shoeDesignFilter.Trim();
            var shown = 0;

            foreach (var (id, name, _) in wardrobe.Pool())
            {
                if (shown >= 8)
                    break;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 || config.RollShoesFor.Contains(id))
                    continue;

                shown++;
                if (!ImGui.Button($"{name}##addshoedesign{id}"))
                    continue;

                config.RollShoesFor.Add(id);
                config.Save();
                shoeDesignFilter = string.Empty;
                break;
            }

            if (shown == 0)
                ImGui.TextColored(Dim, "Nothing matching that is left to add.");
        }

        ImGui.Unindent();
    }

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

        var settle = config.SettleSeconds;
        if (ImGui.SliderInt("Wait after logging in (seconds)", ref settle, 0, 30))
        {
            config.SettleSeconds = Math.Max(0, settle);
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Nobody is touched for this long after you log in or change zone.\n" +
                             "The client is still streaming models and materials in, and a redraw\n" +
                             "asked for in the middle of that is handed a material that has not\n" +
                             "arrived yet - which is a character rendered black, with nothing to\n" +
                             "black, with nothing to ask again. Raise it if you still see one.");

        var onCreate = config.SettleOnCreate;
        if (ImGui.Checkbox("Settle people as the game builds them", ref onCreate))
        {
            config.SettleOnCreate = onCreate;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lets a rebuild keep what the collection already holds, so a spawn or a\n" +
                             "zone-in of somebody already dealt costs no redraw at all, and a retainer\n" +
                             "called up at a quiet bell arrives in her new outfit on the first try.\n" +
                             "Fresh deals wait for model building to go quiet before they are written -\n" +
                             "a write landing on a half-built crowd is what paints people black.");

        var cards = config.MirrorCards;
        if (ImGui.Checkbox("Dress the duty cards", ref cards))
        {
            config.MirrorCards = cards;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The party cards when a duty starts are drawn from the server's gear\n" +
                             "snapshot and Glamourer leaves them alone - so the outfit everyone is\n" +
                             "wearing on your screen is copied onto their card as it appears.\n" +
                             "Gear and dyes carry over; race swaps and busts cannot, those need a\n" +
                             "rebuild the cards never get.");

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

        var drift = config.AllowDrift;
        if (ImGui.Checkbox("Let other people's options drift", ref drift))
        {
            config.AllowDrift = drift;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Penumbra's temporary settings belong to a collection rather than to a\n" +
                             "person, so a mod worn by a dozen people at once has them taking turns to\n" +
                             "redraw each other back to their own roll. Whatever any of them was last\n" +
                             "rebuilt with was a set somebody was meant to be seen in, and nobody can\n" +
                             "tell which of a dozen strangers had which toggle - so this takes it and\n" +
                             "spares the redraw. Everyone still gets their own roll the first time.\n" +
                             "Never applies to you: yours is the one outfit somebody is watching.");

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
            // A mod is not an outfit and cannot be put on by itself, so the nearest thing to
            // trying one on is being dealt something built on it. A different one each press
            // where several would do, since seeing the same outfit again tells you no more.
            ImGui.SameLine();
            var wearers = wardrobe.WearerCount(mod.Directory);
            ImGui.BeginDisabled(wearers == 0);
            if (ImGui.SmallButton("Wear") && wardrobe.AnyWearerOf(mod.Directory) is { } outfit)
                WearNow(outfit.Id, outfit.Path);
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(wearers == 0
                    ? "No outfit in your pool is built on this mod, so there is nothing\n"
                      + "to put on that would show it."
                    : $"Deals you one of the {wearers} outfit(s) built on this mod, rolled as\n"
                      + "anybody would get it. A different one each press.");

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

                var bareMod = config.LeaveChestMods.Contains(mod.Directory);
                if (ImGui.Checkbox("Chest as modelled##barechest", ref bareMod))
                {
                    if (bareMod)
                        config.LeaveChestMods.Add(mod.Directory);
                    else
                        config.LeaveChestMods.Remove(mod.Directory);
                    config.Save();
                    wardrobe.ForgetChestFlags();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This mod sizes its own chest: every design wearing it gets no max\n" +
                                     "bust and no rolled shape, so the mesh is the only thing doing the\n" +
                                     "sizing.");

                // Setting a mod's options at all means saying a priority, so it is either carried
                // across from Penumbra or named here. Named matters for a mod that only wins its
                // files by outranking another: carrying works until the collection says nothing.
                var pinned = mod.Priority.HasValue;
                if (ImGui.Checkbox("Priority##prio", ref pinned))
                {
                    mod.Priority = pinned ? 0 : null;
                    config.Save();
                    wardrobe.ForgetMods();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Unticked, the mod keeps the priority you gave it in Penumbra.\n" +
                                     "Tick it to name one instead - which is what a mod needs when it\n" +
                                     "only shows by outranking another mod over the same files.");

                if (mod.Priority is { } priority)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(90f);
                    if (ImGui.InputInt("##priovalue", ref priority))
                        mod.Priority = priority;

                    // Held until the box is let go. Priority is part of what a collection is
                    // holding, so changing it re-settles everybody in front of you - a redraw
                    // each - and paying that on every click of the stepper, or every keystroke
                    // of a two-digit number, is a crowd redrawn several times over for one edit.
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.Save();
                        wardrobe.ForgetMods();
                    }
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Dim, "as set in Penumbra");
                }

                var together = mod.LinkGroups;
                if (ImGui.Checkbox("Roll matching groups together##link", ref together))
                {
                    mod.LinkGroups = together;
                    config.Save();
                    wardrobe.ForgetMods();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Groups offering the same options answer as one, so a colour spread\n" +
                                     "over seven pieces of an outfit comes out the same on all seven\n" +
                                     "instead of a patchwork. Only what all of them offer can come up -\n" +
                                     "a shade two of the seven have not got cannot be the answer for all.");

                // Which groups it has decided answer as one, said out loud: it is worked out from
                // the options rather than told, so it has to be visible to be trusted.
                var links = mod.LinkGroups
                    ? wardrobe.LinksOf(mod)
                    : [];

                foreach (var (_, shared, members) in links)
                {
                    ImGui.Indent();
                    ImGui.TextColored(Dim, $"{string.Join(", ", members)} - as one, from {shared.Count} option"
                                           + (shared.Count == 1 ? "" : "s"));
                    if (shared.Count > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip("They can agree on:\n  " + string.Join("\n  ", shared));
                    ImGui.Unindent();
                }

                var inLink = links.SelectMany(l => l.Groups).ToHashSet();

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

                    // The kind leads, because it is the thing worth scanning down the list for:
                    // a dropdown that lands on the one option called "None" and tick boxes that
                    // turn something off are the same surprise arrived at two different ways.
                    ImGui.SameLine();
                    ImGui.TextColored(Dim, type == PenumbraIpc.GroupType.Single
                        ? $"- picks ONE of {live}"
                        : $"- flips EACH of {live}");

                    if (inLink.Contains(group))
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Dim, "- linked");
                    }

                    if (live != options.Length)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(Dim, $"({options.Length - live} left out)");
                    }

                    if (include)
                        DrawGroupOptions(mod, group, options, type);
                }

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
            $"{wardrobe.Dressed} dressed right now, {wardrobe.Baked} baked while built, " +
            $"{wardrobe.Remembered} remembered" +
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

        DrawDesignOdds();

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

        var turnPlayers = config.TurnMalePlayers;
        if (ImGui.Checkbox("Men turn up as women", ref turnPlayers))
        {
            config.TurnMalePlayers = turnPlayers;
            config.Save();
            // Switching it on needs nothing - the next pass finds them. Switching it off has to
            // put them back, since nothing else undoes a change that is already applied.
            if (!turnPlayers)
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Changes their gender on your screen only, so the designs have somebody\n" +
                             "to go on. Glamourer picks a face and hair the other gender actually has\n" +
                             "- the numbers do not carry across - so colouring and build follow them\n" +
                             "over and the face will not match. This redraws them once.\n" +
                             "They count as women for \"female characters only\" from then on.");

        var turnNpcs = config.TurnMaleNpcs;
        if (ImGui.Checkbox("Men among the NPCs too", ref turnNpcs))
        {
            config.TurnMaleNpcs = turnNpcs;
            config.Save();
            if (!turnNpcs)
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The same for NPCs, which is a separate decision: a city is mostly men,\n" +
                             "and every one of them turned is a redraw and an outfit.\n" +
                             "Only has an effect while NPCs are being dealt to at all.");

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

        var lala = config.SwapLalafell;
        if (ImGui.Checkbox("Lalafell turn up as Miqo'te", ref lala))
        {
            config.SwapLalafell = lala;
            config.Save();
            if (!lala)
                wardrobe.RevertAll();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The same as the Hrothgar swap and for the same reason: the designs are\n" +
                             "cut for a tall body, and one on a Lalafell is a mesh that does not fit its\n" +
                             "wearer rather than an outfit. Changes their clan on your screen only.\n" +
                             "This redraws them once, and again if something puts them back.");

        if (config.SwapLalafell)
        {
            ImGui.Indent();
            var keeper = config.LalafellClan == 8;
            if (ImGui.RadioButton("Seeker of the Sun", !keeper))
            {
                config.LalafellClan = 7;
                config.Save();
                wardrobe.ForgetRaces();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Keeper of the Moon", keeper))
            {
                config.LalafellClan = 8;
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

        var retainers = config.IncludeRetainers;
        if (ImGui.Checkbox("Deal to retainers", ref retainers))
        {
            config.IncludeRetainers = retainers;
            config.Save();
            if (!retainers)
                wardrobe.RevertKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Retainer);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Yours and anybody else's standing at a summoning bell, by the same\n" +
                             "rules as everyone. A retainer with a class draws from that\n" +
                             "discipline's pool; one without draws from the whole thing.");

        if (config.IncludeRetainers)
        {
            ImGui.Indent();
            var fresh = config.FreshRetainers;
            if (ImGui.Checkbox("A new one every time they are called up", ref fresh))
            {
                config.FreshRetainers = fresh;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Retainers only. Everybody else keeps what they were given so they stay\n" +
                                 "recognisable, which is the whole reason an outfit is remembered - but a\n" +
                                 "retainer is only in the world while you are at the bell, so there is\n" +
                                 "nobody to recognise her from and no reason not to deal again.\n" +
                                 "Her body is left alone; a different shape each time is a different\n" +
                                 "retainer. One you have chosen to keep is left alone entirely.");
            ImGui.Unindent();
        }

        var npcs = config.IncludeNpcs;
        if (ImGui.Checkbox("Deal to NPCs", ref npcs))
        {
            config.IncludeNpcs = npcs;
            config.Save();
            if (!npcs)
                wardrobe.RevertKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only the ones built on a playable body - a design is a list of gear for\n" +
                             "a human skeleton, so beasts and beast tribes are passed over. Hardly any\n" +
                             "of them have a class, so they draw from the whole pool.\n" +
                             "A city has a lot of NPCs in it; this is the noisiest thing here.");

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
            DrawDyeOdds();
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

        DrawShapes();
        DrawShoes();
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
            var bodies = wardrobe.RerollBodies();
            ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Re-rolling {count} remembered outfit(s)"
                + (config.RandomizeShapes && bodies > 0 ? $" and {bodies} body/bodies." : "."));
        }

        if (config.IncludeMe)
        {
            ImGui.SameLine();
            if (ImGui.Button("Re-roll me"))
                ECommons.DalamudServices.Svc.Chat.Print(wardrobe.RerollMe() is { } outfit
                    ? $"[Glam Roulette] Dealing yourself another one. The draw: {outfit}."
                    : "[Glam Roulette] Nothing of yours to re-roll - it may be one you chose to keep.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Put everyone back"))
            wardrobe.RevertAll();

        ImGui.SameLine();
        if (ImGui.Button("Redraw everyone"))
        {
            var built = wardrobe.RedrawEveryone();
            ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Building {built} character(s) again.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For when somebody has come back wrong - a character rendered black is a\n" +
                             "model that was built while its materials were still arriving, and the\n" +
                             "only cure is building it again now that they are here.\n" +
                             "Costs everyone in sight a redraw, which is the point of it.\n" +
                             "Outfits go back on by themselves afterwards; nothing is re-rolled.");

        ImGui.SameLine();
        if (ImGui.Button("Fix black characters"))
        {
            var fixing = wardrobe.FixEveryone();
            ECommons.DalamudServices.Svc.Chat.Print($"[Glam Roulette] Putting {fixing} character(s) back first - "
                + "fresh deals follow in a few seconds.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reverts everyone - race, gender, outfit, the lot - waits a settle, then\n" +
                             "deals afresh. Their rebuild asks for their own files rather than the ones\n" +
                             "the client already gave up on, which is what actually clears a character\n" +
                             "baked black. A plain redraw asks for the same dead files again.\n" +
                             "Also /glamroulette fix.");

        DrawTryOn();

        ImGui.Spacing();
        ImGui.TextColored(Dim, "Right-click a player's name for \"Re-roll outfit\" to re-roll just them.");
        ImGui.TextWrapped("This only changes how other people look on your screen. They see themselves " +
                          "normally, and so does everyone else.");
    }
}
