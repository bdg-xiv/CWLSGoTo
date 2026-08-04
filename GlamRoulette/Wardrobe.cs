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
internal sealed class Wardrobe(Configuration config, GlamourerIpc glamourer, Dyes dyes)
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

    public bool IsPinned(string playerKey) => config.Pinned.Contains(PlayerOf(playerKey));

    /// <summary>Keeps or stops keeping this player's outfits regardless of when they were
    /// last around. Returns what the setting is now.</summary>
    public bool TogglePinned(string playerKey)
    {
        var player = PlayerOf(playerKey);
        var pinned = !config.Pinned.Remove(player);
        if (pinned)
        {
            config.Pinned.Add(player);
            // Stamp them as seen, or a pin applied to someone already past the cutoff would
            // be undone by the very next prune.
            config.LastSeen[player] = DateTime.UtcNow;
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
        var gone = config.LastSeen
            .Where(s => s.Value < cutoff && !config.Pinned.Contains(s.Key))
            .Select(s => s.Key)
            .ToList();

        if (gone.Count == 0)
            return;

        var dropped = 0;
        foreach (var player in gone)
        {
            config.LastSeen.Remove(player);
            foreach (var key in config.Assignments.Keys
                         .Where(k => PlayerOf(k) == player).ToList())
            {
                config.Assignments.Remove(key);
                dropped++;
            }
        }

        config.Save();
        Svc.Log.Information($"[GlamRoulette] Forgot {dropped} outfit(s) from {gone.Count} player(s) not seen in " +
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
    private Guid? DesignFor(string key, JobPools.Group group)
    {
        if (config.Assignments.TryGetValue(key, out var existing))
            return existing;

        var pool = PoolFor(group);
        if (pool.Count == 0)
            return null;

        var chosen = pool[random.Next(pool.Count)].Id;
        config.Assignments[key] = chosen;
        config.Save();
        return chosen;
    }

    /// <summary>
    /// Throws away this player's outfit so the next pass picks a new one. The key carries the
    /// discipline when pools are split, so this clears every discipline they have been seen
    /// on rather than only the one they happen to be wearing.
    /// </summary>
    public bool Reroll(string key)
    {
        var player = PlayerOf(key);
        var stale = config.Assignments.Keys
            .Where(k => k == player || k.StartsWith(player + "#", StringComparison.Ordinal))
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

    public int RerollEverybody()
    {
        var count = config.Assignments.Count;
        config.Assignments.Clear();
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
            if (player.GameObjectId == me.GameObjectId)
                continue;
            if (config.SkipParty && InParty(player))
                continue;
            if (config.FemaleOnly && !IsFemale(player))
                continue;

            present.Add(player.ObjectIndex);

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

            if (DesignFor(key, group) is not { } design)
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
    }

    /// <summary>Puts everyone back as we found them.</summary>
    public void RevertAll()
    {
        foreach (var index in applied.Keys.ToList())
            glamourer.Revert(index);

        applied.Clear();
        lastApplied.Clear();
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
