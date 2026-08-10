using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Decides who wears what, and remembers it. Assignment is by name and world rather than by
/// object index, because an index is only meaningful for as long as the player stays loaded
/// and the whole point here is that it survives them walking away.
/// </summary>
internal sealed class Wardrobe(Configuration config, GlamourerIpc glamourer, Dyes dyes, RaceSwap races,
    ModRoulette mods, Exclusives exclusives, PenumbraIpc penumbra, Shapes shapes, Shoes shoes)
{
    private readonly Random random = new();

    /// <summary>
    /// Object indices we have dressed, what we put them in, and which model it went onto - so we
    /// know what to revert, can tell a re-apply from a first apply, and can see at once when the
    /// game has rebuilt somebody and taken the outfit off with the old model.
    /// </summary>
    private readonly Dictionary<int, (string Key, Guid Design, uint? Shoe, nint Draw)> applied = [];

    private readonly Dictionary<int, DateTime> lastApplied = [];

    /// <summary>
    /// The model each player's mod settings were settled against. A temporary setting only shows
    /// on the model built after it, so the one question worth asking is whether this is still that
    /// model - the pointer changes on every redraw, ours or the game's, and nothing else has to be
    /// looked at while it has not.
    ///
    /// Pending means we have asked for a rebuild and Draw is the model we asked it to replace.
    /// A redraw takes longer than a pass, so the next look very often still finds the old model;
    /// taking that as the rebuild is what had everybody churning, because the real one landed a
    /// pass later, read as somebody else's doing, and bought another redraw. Only a pointer that
    /// has actually changed is the one we asked for.
    /// </summary>
    private readonly Dictionary<string, (nint Draw, bool Pending, DateTime Touched)> settled = [];

    private readonly CollectionState state = new();

    /// <summary>Who Glamourer has already turned down, so it is said once rather than every
    /// pass for as long as they are in front of us.</summary>
    private readonly HashSet<string> refused = [];

    private DateTime nextPrune = DateTime.MinValue;
    private bool seenDirty;

    public int Dressed => applied.Count;
    public int Remembered => config.Assignments.Count;
    public int Kept => config.Pinned.Count;

    /// <summary>The assignment key for a player as they are right now, role included.</summary>
    public string KeyFor(ICharacter character)
    {
        var group = JobPools.GroupOf(character);
        var key = KeyOf(character);
        return config.MatchJobCategory && group != JobPools.Group.Unknown ? key + "#" + group : key;
    }

    /// <summary>Pins from before this was per-outfit named a whole player, so honour both.</summary>
    public bool IsPinned(string assignmentKey)
        => config.Pinned.Contains(assignmentKey) || config.Pinned.Contains(PlayerOf(assignmentKey));

    /// <summary>Keeps or stops keeping this one outfit. Returns what the setting is now.</summary>
    public bool TogglePinned(string assignmentKey)
    {
        bool pinned;
        if (IsPinned(assignmentKey))
        {
            // Clear the whole-player form as well, or an old one would keep every role of
            // theirs pinned and unpinning a single outfit would look like it did nothing.
            config.Pinned.Remove(assignmentKey);
            config.Pinned.Remove(PlayerOf(assignmentKey));
            pinned = false;
        }
        else
        {
            config.Pinned.Add(assignmentKey);
            // Stamp them as seen, or a pin applied to someone already past the cutoff would
            // be undone by the very next prune.
            config.LastSeen[PlayerOf(assignmentKey)] = DateTime.UtcNow;
            pinned = true;
        }

        config.Save();
        return pinned;
    }

    /// <summary>
    /// Assignments from before there was a last-seen time get stamped as if they were just
    /// seen, so switching this on ages everyone out over the next half hour instead of
    /// wiping every outfit at once.
    /// </summary>
    public void StampUnknownAsSeen()
    {
        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var key in config.Assignments.Keys)
        {
            var player = PlayerOf(key);
            if (config.LastSeen.TryAdd(player, now))
                added++;
        }

        if (added > 0)
        {
            config.Save();
            Svc.Log.Information($"[GlamRoulette] {added} remembered outfit(s) had no last-seen time, starting their clock now");
        }
    }

    /// <summary>Drops the outfits of players who have not been around for a while.</summary>
    private void Prune()
    {
        if (config.RememberMinutes <= 0 || DateTime.UtcNow < nextPrune)
            return;

        nextPrune = DateTime.UtcNow.AddMinutes(1);

        if (seenDirty)
        {
            seenDirty = false;
            config.Save();
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-config.RememberMinutes);
        var gone = config.LastSeen.Where(s => s.Value < cutoff).Select(s => s.Key).ToList();
        if (gone.Count == 0)
            return;

        var dropped = 0;
        var players = 0;

        foreach (var player in gone)
        {
            // Pinned outfits survive individually, so a player can keep the one that was
            // worth keeping and lose the rest.
            var expiring = config.Assignments.Keys
                .Where(k => PlayerOf(k) == player && !IsPinned(k))
                .ToList();

            foreach (var key in expiring)
            {
                config.Assignments.Remove(key);
                dropped++;
            }

            if (expiring.Count > 0)
                players++;

            // Only stop tracking them once nothing of theirs is left, or a pinned outfit
            // would lose its last-seen time and never be prunable if it were ever unpinned.
            if (!config.Assignments.Keys.Any(k => PlayerOf(k) == player))
                config.LastSeen.Remove(player);
        }

        if (dropped == 0)
            return;

        config.Save();
        Svc.Log.Information($"[GlamRoulette] Forgot {dropped} outfit(s) from {players} player(s) not seen in " +
                            $"{config.RememberMinutes} minutes");
    }

    /// <summary>
    /// Who an outfit is remembered against. A player is their name and world; the other two have
    /// no world to be from, so they get a word in its place - which also keeps them from ever
    /// colliding with a player who happens to share the name.
    /// </summary>
    public static string KeyOf(ICharacter character)
        => character switch
        {
            IPlayerCharacter player
                => $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText() ?? "?"}",
            { ObjectKind: ObjectKind.Retainer } => $"{character.Name.TextValue}@retainer",
            _ => $"{character.Name.TextValue}@npc",
        };

    private List<(Guid Id, string Name, string Path)>? pool;
    private DateTime pooledAt = DateTime.MinValue;

    /// <summary>
    /// Every design the folder filter allows, ignoring discipline. Held for a few seconds
    /// because it is a round trip into Glamourer for the whole list, and it was being made once
    /// per player per pass - and eight times a frame while the settings window was open.
    /// </summary>
    public List<(Guid Id, string Name, string Path)> Pool()
    {
        if (pool != null && DateTime.UtcNow - pooledAt < TimeSpan.FromSeconds(5))
            return pool;

        var root = config.DesignFolder.Trim().Trim('/');
        var fresh = new List<(Guid, string, string)>();

        foreach (var (id, data) in glamourer.Designs())
        {
            if (!JobPools.IsInFolder(data.FullPath, root))
                continue;

            fresh.Add((id, data.DisplayName, data.FullPath));
        }

        pooledAt = DateTime.UtcNow;
        return pool = fresh;
    }

    /// <summary>Drops the held design list, for when it is known to have changed.</summary>
    public void ForgetPool() => pool = null;

    /// <summary>
    /// Puts a pair of shoes on yourself until they are taken off again, or takes them off. The
    /// pass notices on its own - what somebody has on their feet is part of what they were
    /// dealt, so a changed pair reads as a changed outfit and is put on within the second, mods
    /// and all.
    /// </summary>
    public void TryShoes(uint? item)
    {
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return;

        shoes.TryOn(KeyFor(me), item);
    }

    /// <summary>
    /// The designs a particular discipline may be given: its own subfolder, plus anything
    /// sitting loose in the design folder if those are being shared out. An empty result
    /// falls back to the whole pool rather than leaving the wearer undressed - a missing
    /// subfolder should look like "not set up yet", not like the plugin is broken.
    /// </summary>
    public List<(Guid Id, string Name, string Path)> PoolFor(JobPools.Group group)
    {
        var all = Pool();
        if (!config.MatchJobCategory || group == JobPools.Group.Unknown)
            return all;

        var root = config.DesignFolder.Trim().Trim('/');
        var shared = config.IncludeSharedDesigns
            ? all.Where(d => JobPools.IsDirectlyIn(d.Path, root)).ToList()
            : [];

        // Most specific folder that actually has something in it wins, so a role folder beats
        // its discipline and an empty one is simply passed over rather than emptying the pool.
        foreach (var prefix in JobPools.FoldersFor(config, root, group))
        {
            var pool = all.Where(d => JobPools.IsInFolder(d.Path, prefix)).ToList();
            if (pool.Count == 0)
                continue;

            pool.AddRange(shared);
            return pool;
        }

        return shared.Count > 0 ? shared : all;
    }

    /// <summary>Hands out an outfit, keeping whatever this player was given before.</summary>
    private Guid? DesignFor(string key, JobPools.Group group, Guid? avoid = null)
    {
        if (config.Assignments.TryGetValue(key, out var existing))
            return existing;

        var pool = PoolFor(group);

        // Drawing the one just taken off would look like nothing happened - the dyes are
        // derived from the outfit, so even those would come back the same.
        if (avoid is { } previous && pool.Count > 1)
            pool = pool.Where(d => d.Id != previous).ToList();

        if (pool.Count == 0)
            return null;

        if (Draw(pool) is not { } chosen)
            return null;

        config.Assignments[key] = chosen;

        // Written once at the end of the pass rather than here. Walking into a city deals to
        // everybody in it at once, and each save is the whole config serialised out to disk -
        // forty of those in one frame is a stutter you can feel.
        dealtDirty = true;
        return chosen;
    }

    /// <summary>Somebody was dealt an outfit this pass, so the config wants writing.</summary>
    private bool dealtDirty;

    /// <summary>
    /// Throws away this player's outfits so the next pass picks new ones - every role they
    /// have been seen on, not only the one they are standing in. Kept outfits are left alone;
    /// they are meant to be immovable, and unpinning is right there in the same menu.
    /// </summary>
    public bool Reroll(string key, bool body = true)
    {
        var player = PlayerOf(key);
        var stale = config.Assignments.Keys
            .Where(k => (k == player || k.StartsWith(player + "#", StringComparison.Ordinal)) && !IsPinned(k))
            .ToList();

        // Their body goes back in the pack too, whether or not they had an outfit to lose - it is
        // rolled off the plain name and world, and it is one of the things a re-roll is for.
        if (body)
        {
            Rolled(player);
            shapes.Forget(player);
        }

        if (stale.Count == 0)
        {
            config.Save();
            return false;
        }

        foreach (var k in stale)
        {
            config.Assignments.Remove(k);
            Rolled(k);

            // The mod options are rolled from the same key, so they have to be looked at again
            // as well - a new outfit is very likely a different set of mods.
            settled.Remove(k);
        }

        config.Save();

        // Drop the applied record too, or nothing would re-apply until they reload.
        foreach (var index in applied
                     .Where(a => a.Value.Key == player
                                 || a.Value.Key.StartsWith(player + "#", StringComparison.Ordinal))
                     .Select(a => a.Key).ToList())
        {
            applied.Remove(index);
            lastApplied.Remove(index);
        }

        return true;
    }

    /// <summary>
    /// How often one outfit comes up against the others in its pool. One unless you have said
    /// otherwise, so a pool nobody has weighted draws evenly and this costs nothing.
    /// </summary>
    public int WeightOf(Guid design)
        => config.DesignWeights.TryGetValue(design, out var weight) ? Math.Max(0, weight) : 1;

    /// <summary>Whether this outfit has been given odds of its own.</summary>
    public bool IsWeighted(Guid design) => config.DesignWeights.ContainsKey(design);

    /// <summary>The share of a pool one outfit will take, for the window to show. Worked out
    /// against the whole pool rather than a discipline's, which is the one everyone is drawn
    /// from when the pools are not split and the closest thing to an answer when they are.</summary>
    public float ShareOf(Guid design)
    {
        var total = Pool().Sum(d => (long)WeightOf(d.Id));
        return total == 0 ? 0f : (float)WeightOf(design) / total;
    }

    /// <summary>
    /// Draws one outfit with the odds honoured. Everything weighted to nothing is not "deal
    /// nobody an outfit" - it is somebody having turned every dial down without meaning that -
    /// so it falls back to an even draw rather than leaving the pool undressed.
    /// </summary>
    private Guid? Draw(List<(Guid Id, string Name, string Path)> pool)
    {
        var total = pool.Sum(d => (long)WeightOf(d.Id));
        if (total <= 0)
            return pool[random.Next(pool.Count)].Id;

        var target = (long)(random.NextDouble() * total);
        foreach (var design in pool)
        {
            target -= WeightOf(design.Id);
            if (target < 0)
                return design.Id;
        }

        return pool[^1].Id;
    }

    /// <summary>Counts a re-roll, so the colours come out different even if the same design
    /// comes back round.</summary>
    private void Rolled(string key) => config.Rolls[key] = config.Rolls.GetValueOrDefault(key) + 1;

    private static string PlayerOf(string key)
    {
        var hash = key.IndexOf('#');
        return hash < 0 ? key : key[..hash];
    }

    /// <summary>
    /// Puts one of your own outfits back in the pack once it has been yours long enough, and
    /// returns it so the replacement is a different one. The clock is per outfit rather than per
    /// player: your healer one ageing out leaves your tank one alone, and coming back to a job
    /// after a while is exactly when you find it has changed.
    ///
    /// It runs while you are wearing it too, so sitting on one job long enough changes it where
    /// you stand rather than waiting for you to switch and switch back.
    /// </summary>
    private Guid? ExpireMine(string key)
    {
        if (config.MyRotateMinutes <= 0 || IsPinned(key))
            return null;

        var now = DateTime.UtcNow;

        // First sight of this one: start its clock rather than treating it as ancient.
        if (!config.MyOutfitSince.TryGetValue(key, out var since))
        {
            config.MyOutfitSince[key] = now;
            config.Save();
            return null;
        }

        if (now - since < TimeSpan.FromMinutes(config.MyRotateMinutes))
            return null;

        var previous = config.Assignments.TryGetValue(key, out var worn) ? worn : (Guid?)null;
        config.Assignments.Remove(key);
        Rolled(key);
        config.MyOutfitSince[key] = now;
        config.Save();
        return previous;
    }

    /// <summary>Asks for every race swap again, for when the clan being used changes.</summary>
    public void ForgetRaces() => races.Forget();

    /// <summary>
    /// Throws away your own outfits so the next pass deals you new ones. Same as right-clicking
    /// yourself, without needing to find your own name to right-click.
    /// </summary>
    public bool RerollMe()
    {
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return false;

        // The clock too, or an outfit dealt now would be judged on when the last one arrived
        // and could go stale within the minute.
        var key = KeyOf(me);
        foreach (var stamp in config.MyOutfitSince.Keys.Where(k => PlayerOf(k) == key).ToList())
            config.MyOutfitSince[stamp] = DateTime.UtcNow;

        var dealt = Reroll(key);

        // And the body. That one follows the player rather than the outfit - what job somebody
        // is on has no business changing their shape - so it counts against the bare name and
        // world, and Reroll, which works through the per-job keys, never reaches it. Asking for
        // another of everything and getting the same body back is not another of everything.
        if (config.RandomizeShapes && !IsPinned(key))
        {
            Rolled(key);
            shapes.Forget(key);
            config.Save();
            dealt = true;
        }

        return dealt;
    }

    /// <summary>The outfit you have on right now, if the roulette gave you one.</summary>
    public Guid? MyDesign
    {
        get
        {
            var me = Svc.Objects.LocalPlayer;
            return me != null && config.Assignments.TryGetValue(KeyFor(me), out var design) ? design : null;
        }
    }

    /// <summary>
    /// Puts one particular outfit on yourself rather than waiting for it to come up. Everything
    /// downstream of the outfit is still dealt - the colours, the shoes, the options on the mods
    /// it is built from - so this is a way of seeing one of them as somebody would actually be
    /// dealt it, not a way of bypassing the roulette.
    ///
    /// Asking for the one you already have re-rolls it, since the roll counter goes up either
    /// way. That is the point of it as a way of trying something on: the same outfit twice is
    /// two different sets of options.
    /// </summary>
    public bool WearMyself(Guid design)
    {
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return false;

        // The role-keyed one, because that is the key the pass will look you up under.
        var key = KeyFor(me);
        config.Assignments[key] = design;

        // Its clock starts now, or an outfit put on by hand could be rotated off within the
        // minute for having been dealt at whatever time the last one was.
        config.MyOutfitSince[key] = DateTime.UtcNow;
        Rolled(key);
        config.Save();

        // What we think you are wearing, so the next pass dresses you again rather than deciding
        // there is nothing to do.
        settled.Remove(key);
        foreach (var index in applied.Where(a => a.Value.Key == key).Select(a => a.Key).ToList())
        {
            applied.Remove(index);
            lastApplied.Remove(index);
        }

        return true;
    }

    /// <summary>
    /// Re-rolls every body except the ones whose outfit was explicitly kept. Separate from the
    /// outfits, because most of what re-deals those is the design pool changing - a dye weight,
    /// a folder - and none of that is a reason for everybody to change shape.
    /// </summary>
    public int RerollBodies()
    {
        var players = config.Assignments.Keys
            .Where(k => !IsPinned(k))
            .Select(PlayerOf)
            .Distinct()
            .ToList();

        foreach (var player in players)
            Rolled(player);

        config.Save();

        // Not released - the size is part of what each person was given, so the next pass sees
        // it has changed and swaps them straight over.
        shapes.Reload();
        return players.Count;
    }

    /// <summary>Re-rolls everyone's outfit except the ones that were explicitly kept.</summary>
    public int RerollEverybody()
    {
        var stale = config.Assignments.Keys.Where(k => !IsPinned(k)).ToList();
        var count = stale.Count;

        foreach (var key in stale)
        {
            config.Assignments.Remove(key);
            Rolled(key);
        }

        config.Save();
        applied.Clear();
        lastApplied.Clear();
        settled.Clear();
        return count;
    }

    public void Update()
    {
        if (!config.Enabled || !glamourer.Available)
            return;

        Prune();
        shapes.Watch();

        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return;

        var present = new HashSet<int>();
        var here = new HashSet<string>();
        var redrawn = 0;
        var spent = false;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not ICharacter character)
                continue;

            // Yourself is governed by its own setting and nothing else. You are in your own
            // party, so leaving party members alone would otherwise quietly cancel it the
            // moment anyone grouped with you.
            var isMe = character.GameObjectId == me.GameObjectId;
            if (!Eligible(character, isMe))
                continue;

            present.Add(character.ObjectIndex);

            // Someone who has only just come into view reads with no world and no job for a
            // moment. Both go into the key an outfit is remembered against, so acting now means
            // dressing them as "Name@?" and then again as "Name@Coeurl#Tank" once the rest
            // arrives - two keys, two outfits, and it looks like the roulette re-rolling them
            // over and over. They are worth waiting a pass for.
            //
            // Only players: an NPC having no class is not a slow read, it is simply an NPC, and
            // waiting for one to turn up would leave every last one of them undressed.
            if (character is IPlayerCharacter loading
                && (loading.ClassJob.RowId == 0 || loading.HomeWorld.ValueNullable is null))
                continue;

            // Nothing to hang an outfit on. Every key here is built out of the name, and the
            // nameless ones are scenery rather than anybody.
            if (character.Name.TextValue.Length == 0)
                continue;

            // Ahead of dressing them, and worth a pass of its own when it first lands: changing
            // race redraws the character, and a redraw takes the outfit off again. Better to
            // let the next pass dress the Elezen than to dress a Hrothgar who is about to stop
            // being one.
            if (races.Handle(character))
            {
                applied.Remove(character.ObjectIndex);
                lastApplied.Remove(character.ObjectIndex);
                continue;
            }

            // Someone being shown as a woman counts as one here. Glamourer changes the model
            // without rewriting the customize data underneath, so they still read as a man in
            // what we can see - and passing them over would mean turning them and then dressing
            // nobody, which is the worst of both.
            if (config.FemaleOnly && !IsFemale(character) && !races.Feminising(character))
                continue;

            // Written in memory every pass but only saved on the prune tick - the config is
            // not worth rewriting once a second for a timestamp.
            var person = KeyOf(character);
            config.LastSeen[person] = DateTime.UtcNow;
            seenDirty = true;

            // Their body, which follows the player rather than the outfit: what job somebody is
            // on has no business changing their shape. Nothing here costs a redraw - Customize+
            // works on the bones every frame - so it is done before anything that might not
            // happen this pass.
            shapes.Apply(character.ObjectIndex, person, config.Rolls.GetValueOrDefault(person));

            // The discipline is part of the key when pools are split, so switching from a
            // warrior to a weaver gets an outfit from the right pool instead of keeping the
            // one they were given as a warrior. Anyone with no class at all - which is most
            // NPCs - falls through to the whole pool.
            var group = JobPools.GroupOf(character);
            var key = person;
            if (config.MatchJobCategory && group != JobPools.Group.Unknown)
                key += "#" + group;

            // Only your own outfits go stale. Everyone else's are meant to stick.
            if (DesignFor(key, group, isMe ? ExpireMine(key) : null) is not { } design)
                continue;

            here.Add(key);

            // Worked out here rather than at the moment they are put on, because what an
            // outfit is wearing decides which mods get rolled for it and which of a clashing
            // pair it belongs to - a rolled pair of heels can be the whole reason a mod is in
            // play, so it has to be known before any of that is settled.
            var shoe = shoes.For(key, design, config.Rolls.GetValueOrDefault(key));

            // Mod settings only show on the model built after them, so the one question worth
            // asking is whether this is still the model they were settled against. While it is,
            // there is nothing to work out and nothing to send.
            //
            // Someone still loading in has no model to ask about, and that is not a reason to
            // leave them undressed - Glamourer holds an outfit for a character who has yet to
            // appear and puts it on as they do, which is how they come into view already
            // wearing it rather than changing a second later.
            var draw = ModelOf(character);
            if (draw != 0)
            {
                if (!settled.TryGetValue(key, out var mark))
                {
                    // Never settled: they get their own roll, whoever they are. This is the one
                    // that buys the variety, and it is one redraw per person per mod, once.
                    if (!Settle(character, key, design, shoe, draw, false, ref spent, ref redrawn))
                        continue;
                }
                else if (mark.Pending)
                {
                    // Still the model we asked to have replaced, so the rebuild has not landed
                    // yet. Waiting costs a pass; taking this one costs a redraw a pass later.
                    if (draw != mark.Draw)
                        settled[key] = (draw, false, DateTime.UtcNow);
                }
                else if (mark.Draw != draw)
                {
                    // Something rebuilt them - a zone change, a gearset, someone else's
                    // business. Whatever they were carrying went with the old model, and what
                    // they were rebuilt with is whatever the collection holds now.
                    //
                    // For anybody but you that is somebody else's roll of the same mod, which
                    // is a set of options they were meant to be seen in - not theirs, but not
                    // wrong either, and nobody can tell which of a dozen strangers had which
                    // armband. Taking it saves the redraw that would otherwise be answered by
                    // the next person's redraw, and theirs by the next, which is where a mod
                    // worn by thirteen people at once goes.
                    //
                    // Yours is never let go: it is the one outfit somebody is watching, and
                    // having it change out from under you is the bug rather than the fix.
                    var drifting = config.AllowDrift && !isMe;
                    if (!Settle(character, key, design, shoe, draw, drifting, ref spent, ref redrawn))
                        continue;
                }
                else
                {
                    settled[key] = (mark.Draw, false, DateTime.UtcNow);
                }
            }

            if (applied.TryGetValue(character.ObjectIndex, out var current)
                && current.Key == key && current.Design == design && current.Shoe == shoe && current.Draw == draw)
            {
                if (!config.Reapply)
                    continue;

                // Anything that redraws a character drops the design, and Glamourer will not
                // put it back by itself, so this quietly nails it back on.
                if (lastApplied.TryGetValue(character.ObjectIndex, out var when)
                    && DateTime.UtcNow - when < TimeSpan.FromSeconds(config.ReapplySeconds))
                    continue;
            }

            var result = glamourer.Apply(design, character.ObjectIndex);
            lastApplied[character.ObjectIndex] = DateTime.UtcNow;

            if (result is GlamourerIpc.Result.Success or GlamourerIpc.Result.NothingDone)
            {
                refused.Remove(key);
                applied[character.ObjectIndex] = (key, design, shoe, draw);

                // Has to follow every apply, not just the first: applying the design puts the
                // design's own dyes back on, so the re-dye would be undone by the next pass.
                dyes.Apply(character.ObjectIndex, key, design, config.Rolls.GetValueOrDefault(key), shoe);
            }
            else if (result == GlamourerIpc.Result.DesignNotFound)
            {
                // The design was deleted in Glamourer since we noted it down. Forget the
                // assignment so the next pass draws a fresh one rather than failing forever.
                // Only the outfit: a design you deleted says nothing about anybody's body.
                Svc.Log.Information($"[GlamRoulette] {key}'s design no longer exists, re-rolling them");
                Reroll(key, body: false);
            }
            else if (refused.Add(key))
            {
                // Somebody Glamourer will not let us touch stays exactly as they were - no
                // outfit, no colours, and no turning them either, since that goes through the
                // same door. Watching one of them and seeing nothing happen tells you nothing
                // was tried; this says what was tried and what came back. Once per person, since
                // it is asked again every pass for as long as they stand there.
                Svc.Log.Warning($"[GlamRoulette] Glamourer would not dress {key}: {result} - "
                                + GlamourerIpc.Explain(result));
            }
        }

        if (dealtDirty)
        {
            dealtDirty = false;
            config.Save();
        }

        // Forget people who have left, so they get re-applied on sight rather than being
        // assumed to still be wearing it.
        foreach (var index in applied.Keys.Where(i => !present.Contains(i)).ToList())
        {
            applied.Remove(index);
            lastApplied.Remove(index);
        }

        // Dropped on age rather than on being missing from a single pass. Somebody skipped for
        // a moment - mid race change, customize data not read yet, out of the table while being
        // rebuilt - is not somebody who left, and forgetting them meant working them out again
        // from scratch, finding the collection had moved on, and buying a redraw for it. That
        // was most of the churn. A pointer held too long costs nothing: it will simply not match
        // when they come back, and they settle again.
        var stale = DateTime.UtcNow.AddMinutes(-5);
        foreach (var key in settled.Where(s => !here.Contains(s.Key) && s.Value.Touched < stale)
                     .Select(s => s.Key).ToList())
            settled.Remove(key);

        races.Sweep(present);
    }

    /// <summary>
    /// Why each character in front of you is or is not being dealt with, in the order the pass
    /// asks. Every one of these questions is a silent skip during a pass - which is right, since
    /// most of them are asked of hundreds of people a second - and that leaves watching somebody
    /// stay exactly as they are the only way to find out anything, which tells you nothing at
    /// all. This is the same questions asked out loud, once, on request.
    /// </summary>
    public IReadOnlyList<string> Explain()
    {
        var me = Svc.Objects.LocalPlayer;
        var lines = new List<string>();

        foreach (var obj in Svc.Objects)
        {
            if (obj is not ICharacter character)
                continue;

            var kind = character.ObjectKind;
            if (kind is not (ObjectKind.Pc or ObjectKind.Retainer or ObjectKind.EventNpc or ObjectKind.BattleNpc))
                continue;

            var name = character.Name.TextValue;
            var who = $"{(name.Length > 0 ? name : "(no name)")} [{kind}]";
            var isMe = me != null && character.GameObjectId == me.GameObjectId;

            string reason;
            if (kind == ObjectKind.Pc && isMe && !config.IncludeMe)
                reason = "you, and you are not taking a turn";
            else if (kind == ObjectKind.Pc && !isMe && config.SkipParty && InParty(character))
                reason = "in your party, which you are leaving alone";
            else if (kind == ObjectKind.Retainer && !config.IncludeRetainers)
                reason = "a retainer, which you are not dealing to";
            else if (kind is ObjectKind.EventNpc or ObjectKind.BattleNpc && !config.IncludeNpcs)
                reason = "an NPC, which you are not dealing to";
            else if (!IsPlayerLike(character))
                reason = "not built on a playable body";
            else if (character is IPlayerCharacter p && p.ClassJob.RowId == 0)
                reason = "no class read yet - the game has not sent it";
            else if (character is IPlayerCharacter w && w.HomeWorld.ValueNullable is null)
                reason = "no world read yet - the game has not sent it";
            else if (name.Length == 0)
                reason = "no name to remember them by";
            else if (character.Customize.Length <= (int)CustomizeIndex.Gender)
                reason = "no customize data read yet - the game has not sent it";
            else if (config.FemaleOnly && !IsFemale(character) && !races.Feminising(character))
                reason = "a man, and men are not being turned for this kind";
            else
                reason = config.Assignments.TryGetValue(KeyFor(character), out var design)
                    ? $"DEALT - {(applied.ContainsKey(character.ObjectIndex) ? "wearing" : "not on yet")} {design}"
                    : "eligible, waiting for an outfit";

            lines.Add($"{who}: {reason}");
        }

        return lines;
    }

    /// <summary>
    /// Whether this one gets a turn at all. Players are the default; retainers and NPCs are each
    /// switched on separately, and both only while they are built on a playable body.
    /// </summary>
    private bool Eligible(ICharacter character, bool isMe)
    {
        switch (character.ObjectKind)
        {
            case ObjectKind.Pc when isMe:
                if (!config.IncludeMe)
                    return false;
                break;

            case ObjectKind.Pc:
                if (config.SkipParty && InParty(character))
                    return false;
                break;

            case ObjectKind.Retainer:
                if (!config.IncludeRetainers)
                    return false;
                break;

            case ObjectKind.EventNpc or ObjectKind.BattleNpc:
                if (!config.IncludeNpcs)
                    return false;
                break;

            // Minions, mounts, ornaments, treasure coffers. Nothing there to dress.
            default:
                return false;
        }

        return IsPlayerLike(character);
    }

    /// <summary>Which of the game's character models are the playable one, read once.</summary>
    private static HashSet<uint>? humanModels;

    /// <summary>
    /// Whether this one is a playable body rather than a beast. The game's own ModelChara sheet
    /// says which: type 1 is the human model, the one built out of a customize array, and it is
    /// the only one a design's gear means anything on. Everything else - a goobbue, an amalj'aa,
    /// the dragon somebody is riding - has its own model with its own slots, and dressing it is
    /// at best nothing happening.
    ///
    /// Read straight off the object rather than off what is drawn, so someone still loading in is
    /// judged correctly instead of being passed over for having no model yet. Failing to read the
    /// sheet at all lets everyone through: this is here to spare the odd monster, not to be the
    /// thing that quietly stops anybody being dressed.
    /// </summary>
    private static unsafe bool IsPlayerLike(ICharacter character)
    {
        humanModels ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>()
            .Where(m => m.Type == 1)
            .Select(m => m.RowId)
            .ToHashSet();

        if (humanModels.Count == 0)
            return true;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)character.Address;
        return native != null && humanModels.Contains((uint)native->ModelContainer.ModelCharaId);
    }

    /// <summary>
    /// Brings one player's mods to what their outfit wants. Returns false when they are to be
    /// left for a later pass, either because they are being rebuilt or because this pass has
    /// done enough of that already.
    /// </summary>
    private bool Settle(ICharacter character, string key, Guid design, uint? shoe, nint draw,
        bool drifting, ref bool spent, ref int redrawn)
    {
        // Which collection they are really being drawn with. Asked for only when something has
        // actually rebuilt them, since it is a round trip and the answer rarely changes.
        var collection = penumbra.CollectionOf(character.ObjectIndex);
        var wishes = Wishes(collection, key, design, shoe, config.Rolls.GetValueOrDefault(key));

        // Only what the collection is not already holding. A zone change throws away every model
        // in it but not the collection's settings, so whoever's options were loaded before the
        // teleport comes back correct on the other side without being touched - which is most of
        // a hunt train, most of the time.
        //
        // While drifting, a mod we already have switched on to something counts as answered
        // whoever it was answered for. Only the ones that have to be off are still insisted on:
        // a mod switched off to stop it fighting another is not interchangeable with the same
        // mod switched on, and letting that one drift is two outfits over the same item.
        var missing = wishes
            .Where(w => !state.Holds(collection, w.Mod, w.Signature)
                        && !(drifting && w.Enabled && state.Carries(collection, w.Mod)))
            .ToList();

        if (missing.Count == 0)
        {
            settled[key] = (draw, false, DateTime.UtcNow);
            return true;
        }

        // One person's worth of upheaval per pass. A crowd arriving together would otherwise
        // take all of their redraws in the same frame, which is the freeze.
        if (spent)
            return false;

        foreach (var (mod, enabled, priority, options, signature) in missing)
            if (penumbra.Apply(character.ObjectIndex, mod, enabled, priority, options))
                state.Wrote(collection, mod, signature, enabled);

        // The settings are in place either way. Forcing the redraw is only about showing them now
        // rather than whenever the game next reloads that character on its own.
        if (!config.ForceRedraw)
        {
            settled[key] = (draw, false, DateTime.UtcNow);
            return true;
        }

        // Said out loud, because a redraw is the one thing here anybody can see happening and
        // there is no other way to tell ours from somebody else's.
        Svc.Log.Information($"[GlamRoulette] Redrawing {key} for {string.Join(", ", missing.Select(m => m.Mod))}");

        // The redraw takes the outfit off with the old model, so it goes on next pass rather
        // than being put on now and immediately thrown away.
        penumbra.Redraw(character.ObjectIndex);
        applied.Remove(character.ObjectIndex);
        lastApplied.Remove(character.ObjectIndex);

        // The model we are replacing, so the rebuild can be told from it.
        settled[key] = (draw, true, DateTime.UtcNow);

        if (!config.RedrawAllAtOnce && ++redrawn >= config.RedrawsPerPass)
            spent = true;

        return false;
    }

    /// <summary>
    /// Everything one outfit needs of the mods it is built on, the clash handling and the rolled
    /// options merged into one list so a person costs one redraw rather than two. Off wins: a mod
    /// switched off to stop it fighting is not one whose options are worth rolling.
    /// </summary>
    private List<(string Mod, bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Options, string Signature)>
        Wishes(Guid collection, string key, Guid design, uint? shoe, int roll)
    {
        var none = new Dictionary<string, IReadOnlyList<string>>();
        var wishes = new Dictionary<string, (bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Options)>();

        // Priority does not matter for one being switched off, and there is nothing to carry it
        // from either - the clash list says only which of a pair belongs to this outfit.
        foreach (var (mod, enabled) in exclusives.Plan(design, shoe))
            wishes[mod] = (enabled, 0, none);

        foreach (var (mod, priority, options) in mods.Plan(collection, key, design, shoe, roll))
        {
            if (wishes.TryGetValue(mod, out var already) && !already.Enabled)
                continue;

            wishes[mod] = (true, priority, options);
        }

        return wishes
            .Select(w => (w.Key, w.Value.Enabled, w.Value.Priority, w.Value.Options,
                Signature(w.Value.Enabled, w.Value.Priority, w.Value.Options)))
            .ToList();
    }

    /// <summary>What a wish amounts to once it is in the collection, for telling whether it is
    /// already there. Sorted, since a dictionary is not required to hand things back in any
    /// particular order and the same wish has to read the same way twice.</summary>
    private static string Signature(bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options)
        => enabled
            ? $"on @{priority} " + string.Join(",", options
                .OrderBy(o => o.Key, StringComparer.Ordinal)
                .Select(o => $"{o.Key}:{string.Join("/", o.Value)}"))
            : "off";

    /// <summary>
    /// The model a player is currently being drawn as, or zero while they have none. This is what
    /// a mod setting is baked into, so a new one means everything they were carrying is gone -
    /// which is the whole of what a zone change does to a crowd.
    /// </summary>
    private static unsafe nint ModelOf(ICharacter character)
    {
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)character.Address;
        return native == null ? 0 : (nint)native->DrawObject;
    }

    /// <summary>Puts everyone back as we found them.</summary>
    public void RevertAll()
    {
        // Race and outfit both come off with the same call, and someone can have had one
        // without the other - a Hrothgar with nothing in the pool to wear, say.
        var touched = applied.Keys.Concat(races.Indices).Distinct().ToList();
        foreach (var index in touched)
            Restore(index);

        // The settings belong to the collections rather than to the people, so they come out by
        // collection - and then everyone we dressed needs rebuilding to stop wearing them.
        var released = ReleaseSettings();
        if (released)
            foreach (var index in touched)
                penumbra.Redraw(index);

        exclusives.Forget();
        races.Forget();
        shapes.ReleaseAll();
        applied.Clear();
        lastApplied.Clear();
    }

    /// <summary>
    /// Puts back only the ones of a given kind, for when retainers or NPCs stop being dealt to.
    /// Reverting the lot would work too, but everyone else would be undressed and dressed again
    /// on the next pass for no reason - and that is a flicker on every screen in sight of you.
    /// </summary>
    public void RevertKind(params ObjectKind[] kinds)
    {
        foreach (var index in applied.Keys.ToList())
        {
            if (Svc.Objects[index] is not { } obj || Array.IndexOf(kinds, obj.ObjectKind) < 0)
                continue;

            Restore(index);
            applied.Remove(index);
            lastApplied.Remove(index);
        }
    }

    /// <summary>Hands the shapes out again, for when the profile changes.</summary>
    public void ForgetShapes() => shapes.Reload();

    /// <summary>Takes the shapes back off, for when the whole thing is switched off.</summary>
    public void RevertShapes() => shapes.ReleaseAll();

    /// <summary>The chest bones of the chosen profile that are being rolled, for the window to
    /// name - which of them a profile uses is its own business.</summary>
    public IReadOnlyCollection<string> RolledBones => shapes.Rolling;

    public int Shaped => shapes.Shaped;

    /// <summary>Takes our temporary settings back out of every collection we put them into.
    /// Returns whether there was anything to take out.</summary>
    private bool ReleaseSettings()
    {
        var collections = state.Collections;
        foreach (var collection in collections)
            penumbra.Release(collection);

        state.Forget();
        settled.Clear();
        return collections.Count > 0;
    }

    /// <summary>Rolls the mod options again for everyone, for when the picks change.</summary>
    public void ForgetMods()
    {
        mods.Forget();
        exclusives.Forget();

        // What we thought each collection was holding was worked out from the picks that have
        // just changed, so it is worth nothing now.
        state.Forget();
        settled.Clear();
    }

    /// <summary>How many of the listed clashing mods have a rival among the others.</summary>
    public int ClashCount => exclusives.Clashing;

    /// <summary>A mod's option groups, for the window to list.</summary>
    public IReadOnlyDictionary<string, (string[] Options, PenumbraIpc.GroupType Type)> GroupsOf(string modDirectory)
        => mods.GroupsOf(modDirectory);

    /// <summary>The mod an option says it needs alongside it, for the window to name.</summary>
    public (string Directory, string Name)? CompanionOf(string option) => mods.CompanionOf(option);

    /// <summary>Which of a mod's groups answer as one, for the window to show.</summary>
    public IReadOnlyList<(string Key, IReadOnlyList<string> Shared, IReadOnlyList<string> Groups)> LinksOf(ModPick mod)
        => mods.LinksOf(mod);

    /// <summary>Stops taking part yourself, without disturbing anybody else.</summary>
    public void RevertMe()
    {
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return;

        Restore(me.ObjectIndex);
        applied.Remove(me.ObjectIndex);
        lastApplied.Remove(me.ObjectIndex);
    }

    /// <summary>
    /// Hands someone back. Going by way of the automation is what "back to normal" means for
    /// anyone who has an automated design, since a plain revert would take that off as well and
    /// leave them in whatever they are actually wearing.
    /// </summary>
    private void Restore(int index)
    {
        if (config.RestoreAutomation
            && glamourer.RevertToAutomation(index) is GlamourerIpc.Result.Success or GlamourerIpc.Result.NothingDone)
            return;

        glamourer.Revert(index);
    }

    private static bool InParty(ICharacter character)
        => Svc.Party.Any(member => member.GameObject?.GameObjectId == character.GameObjectId);

    /// <summary>
    /// Gender lives at index 1 of the customize data, 0 male and 1 female. A character whose
    /// data has not streamed in yet reads as nothing rather than as male, so it is skipped and
    /// picked up on a later pass instead of being wrongly dressed or wrongly spared.
    /// </summary>
    private static bool IsFemale(ICharacter character)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Gender
               && customize[(int)CustomizeIndex.Gender] == 1;
    }
}
