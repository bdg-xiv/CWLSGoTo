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

    public int Dressed => applied.Count;
    public int Remembered => config.Assignments.Count;

    public static string KeyOf(IPlayerCharacter player)
        => $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText() ?? "?"}";

    /// <summary>The designs the pool draws from, after the folder filter.</summary>
    public List<(Guid Id, string Name, string Path)> Pool()
    {
        var pool = new List<(Guid, string, string)>();
        foreach (var (id, data) in glamourer.Designs())
        {
            if (config.DesignFolder.Length > 0
                && !data.FullPath.StartsWith(config.DesignFolder, StringComparison.OrdinalIgnoreCase))
                continue;

            pool.Add((id, data.DisplayName, data.FullPath));
        }

        return pool;
    }

    /// <summary>Hands out an outfit, keeping whatever this player was given before.</summary>
    private Guid? DesignFor(string key)
    {
        if (config.Assignments.TryGetValue(key, out var existing))
            return existing;

        var pool = Pool();
        if (pool.Count == 0)
            return null;

        var chosen = pool[random.Next(pool.Count)].Id;
        config.Assignments[key] = chosen;
        config.Save();
        return chosen;
    }

    /// <summary>Throws away this player's outfit so the next pass picks a new one.</summary>
    public bool Reroll(string key)
    {
        if (!config.Assignments.Remove(key))
            return false;

        config.Save();

        // Drop the applied record too, or nothing would re-apply until they reload.
        foreach (var index in applied.Where(a => a.Value.Key == key).Select(a => a.Key).ToList())
        {
            applied.Remove(index);
            lastApplied.Remove(index);
        }

        return true;
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

            var key = KeyOf(player);
            if (DesignFor(key) is not { } design)
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
