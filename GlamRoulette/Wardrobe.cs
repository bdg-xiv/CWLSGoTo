using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Decides who wears what, and remembers it. Assignment is by name and world rather than by
/// object index, because an index is only meaningful for as long as the player stays loaded
/// and the whole point here is that it survives them walking away.
/// </summary>
internal sealed class Wardrobe(Configuration config, GlamourerIpc glamourer, Dyes dyes, RaceSwap races)
{
    private readonly Random random = new();

    /// <summary>Object indices we have dressed, and what we put them in - so we know what to
    /// revert, and can tell a re-apply from a first apply.</summary>
    private readonly Dictionary<int, (string Key, Guid Design)> applied = [];

    private readonly Dictionary<int, DateTime> lastApplied = [];

    private DateTime nextPrune = DateTime.MinValue;
    private bool seenDirty;

    public int Dressed => applied.Count;
    public int Remembered => config.Assignments.Count;
    public int Kept => config.Pinned.Count;

    /// <summary>The assignment key for a player as they are right now, role included.</summary>
    public string KeyFor(IPlayerCharacter player)
    {
        var group = JobPools.GroupOf(player);
        var key = KeyOf(player);
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

    public static string KeyOf(IPlayerCharacter player)
        => $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText() ?? "?"}";

    /// <summary>Every design the folder filter allows, ignoring discipline.</summary>
    public List<(Guid Id, string Name, string Path)> Pool()
    {
        var root = config.DesignFolder.Trim().Trim('/');
        var pool = new List<(Guid, string, string)>();

        foreach (var (id, data) in glamourer.Designs())
        {
            if (!JobPools.IsInFolder(data.FullPath, root))
                continue;

            pool.Add((id, data.DisplayName, data.FullPath));
        }

        return pool;
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

        var chosen = pool[random.Next(pool.Count)].Id;
        config.Assignments[key] = chosen;
        config.Save();
        return chosen;
    }

    /// <summary>
    /// Throws away this player's outfits so the next pass picks new ones - every role they
    /// have been seen on, not only the one they are standing in. Kept outfits are left alone;
    /// they are meant to be immovable, and unpinning is right there in the same menu.
    /// </summary>
    public bool Reroll(string key)
    {
        var player = PlayerOf(key);
        var stale = config.Assignments.Keys
            .Where(k => (k == player || k.StartsWith(player + "#", StringComparison.Ordinal)) && !IsPinned(k))
            .ToList();

        if (stale.Count == 0)
            return false;

        foreach (var k in stale)
            config.Assignments.Remove(k);

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
        config.MyOutfitSince[key] = now;
        config.Save();
        return previous;
    }

    /// <summary>Asks for every race swap again, for when the clan being used changes.</summary>
    public void ForgetRaces() => races.Forget();

    /// <summary>Re-rolls everyone except the outfits that were explicitly kept.</summary>
    public int RerollEverybody()
    {
        var stale = config.Assignments.Keys.Where(k => !IsPinned(k)).ToList();
        var count = stale.Count;

        foreach (var key in stale)
            config.Assignments.Remove(key);

        config.Save();
        applied.Clear();
        lastApplied.Clear();
        return count;
    }

    public void Update()
    {
        if (!config.Enabled || !glamourer.Available)
            return;

        Prune();

        var me = Svc.Objects.LocalPlayer;
        if (me == null)
            return;

        var present = new HashSet<int>();

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            // Yourself is governed by its own setting and nothing else. You are in your own
            // party, so leaving party members alone would otherwise quietly cancel it the
            // moment anyone grouped with you.
            var isMe = player.GameObjectId == me.GameObjectId;
            if (isMe)
            {
                if (!config.IncludeMe)
                    continue;
            }
            else if (config.SkipParty && InParty(player))
            {
                continue;
            }

            present.Add(player.ObjectIndex);

            // Ahead of dressing them, and worth a pass of its own when it first lands: changing
            // race redraws the character, and a redraw takes the outfit off again. Better to
            // let the next pass dress the Elezen than to dress a Hrothgar who is about to stop
            // being one.
            if (races.Handle(player))
            {
                applied.Remove(player.ObjectIndex);
                lastApplied.Remove(player.ObjectIndex);
                continue;
            }

            if (config.FemaleOnly && !IsFemale(player))
                continue;

            // Written in memory every pass but only saved on the prune tick - the config is
            // not worth rewriting once a second for a timestamp.
            config.LastSeen[KeyOf(player)] = DateTime.UtcNow;
            seenDirty = true;

            // The discipline is part of the key when pools are split, so switching from a
            // warrior to a weaver gets an outfit from the right pool instead of keeping the
            // one they were given as a warrior.
            var group = JobPools.GroupOf(player);
            var key = KeyOf(player);
            if (config.MatchJobCategory && group != JobPools.Group.Unknown)
                key += "#" + group;

            // Only your own outfits go stale. Everyone else's are meant to stick.
            if (DesignFor(key, group, isMe ? ExpireMine(key) : null) is not { } design)
                continue;

            if (applied.TryGetValue(player.ObjectIndex, out var current)
                && current.Key == key && current.Design == design)
            {
                if (!config.Reapply)
                    continue;

                // Anything that redraws a character drops the design, and Glamourer will not
                // put it back by itself, so this quietly nails it back on.
                if (lastApplied.TryGetValue(player.ObjectIndex, out var when)
                    && DateTime.UtcNow - when < TimeSpan.FromSeconds(config.ReapplySeconds))
                    continue;
            }

            var result = glamourer.Apply(design, player.ObjectIndex);
            lastApplied[player.ObjectIndex] = DateTime.UtcNow;

            if (result is GlamourerIpc.Result.Success or GlamourerIpc.Result.NothingDone)
            {
                applied[player.ObjectIndex] = (key, design);

                // Has to follow every apply, not just the first: applying the design puts the
                // design's own dyes back on, so the re-dye would be undone by the next pass.
                dyes.Apply(player.ObjectIndex, key, design);
            }
            else if (result == GlamourerIpc.Result.DesignNotFound)
            {
                // The design was deleted in Glamourer since we noted it down. Forget the
                // assignment so the next pass draws a fresh one rather than failing forever.
                Svc.Log.Information($"[GlamRoulette] {key}'s design no longer exists, re-rolling them");
                Reroll(key);
            }
        }

        // Forget people who have left, so they get re-applied on sight rather than being
        // assumed to still be wearing it.
        foreach (var index in applied.Keys.Where(i => !present.Contains(i)).ToList())
        {
            applied.Remove(index);
            lastApplied.Remove(index);
        }

        races.Sweep(present);
    }

    /// <summary>Puts everyone back as we found them.</summary>
    public void RevertAll()
    {
        // Race and outfit both come off with the same call, and someone can have had one
        // without the other - a Hrothgar with nothing in the pool to wear, say.
        foreach (var index in applied.Keys.Concat(races.Indices).Distinct().ToList())
            Restore(index);

        races.Forget();
        applied.Clear();
        lastApplied.Clear();
    }

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

    private static bool InParty(IPlayerCharacter player)
        => Svc.Party.Any(member => member.GameObject?.GameObjectId == player.GameObjectId);

    /// <summary>
    /// Gender lives at index 1 of the customize data, 0 male and 1 female. A character whose
    /// data has not streamed in yet reads as nothing rather than as male, so it is skipped and
    /// picked up on a later pass instead of being wrongly dressed or wrongly spared.
    /// </summary>
    private static bool IsFemale(IPlayerCharacter player)
    {
        var customize = player.Customize;
        return customize.Length > (int)CustomizeIndex.Gender
               && customize[(int)CustomizeIndex.Gender] == 1;
    }
}
