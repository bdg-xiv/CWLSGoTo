using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;

namespace GlamRoulette;

/// <summary>
/// Splits the design pool by what the wearer is. Everything here comes from the game's own
/// ClassJobCategory and Role columns rather than from a table of job names, so a job added in
/// a future patch lands in the right pool without this needing to know it exists.
/// </summary>
internal static class JobPools
{
    private const uint DisciplineOfWar = 30;
    private const uint DisciplineOfMagic = 31;
    private const uint DisciplineOfTheLand = 32;
    private const uint DisciplineOfTheHand = 33;

    private const byte RoleTank = 1;
    private const byte RoleMelee = 2;
    private const byte RoleRanged = 3;
    private const byte RoleHealer = 4;

    public enum Group
    {
        Unknown,
        Tank,
        Melee,
        Ranged,
        Caster,
        Healer,
        Crafter,
        Gatherer,
    }

    /// <summary>
    /// Role alone cannot tell a bard from a black mage - the game files both under ranged -
    /// so the discipline breaks that tie: ranged plus Disciple of Magic is a caster, ranged
    /// plus Disciple of War is a physical ranged.
    ///
    /// Most NPCs have no class at all and come back unknown, which is what puts them in front of
    /// the whole pool rather than one discipline's share of it.
    /// </summary>
    public static Group GroupOf(ICharacter character)
    {
        if (character.ClassJob.ValueNullable is not { } job)
            return Group.Unknown;

        var category = job.ClassJobCategory.RowId;
        return category switch
        {
            DisciplineOfTheHand => Group.Crafter,
            DisciplineOfTheLand => Group.Gatherer,
            _ => job.Role switch
            {
                RoleTank => Group.Tank,
                RoleMelee => Group.Melee,
                RoleRanged => category == DisciplineOfMagic ? Group.Caster : Group.Ranged,
                RoleHealer => Group.Healer,
                _ => Group.Unknown,
            },
        };
    }

    public static string Label(Group group)
        => group switch
        {
            Group.Tank => "Tanks",
            Group.Melee => "Melee DPS",
            Group.Ranged => "Physical ranged",
            Group.Caster => "Magical ranged",
            Group.Healer => "Healers",
            Group.Crafter => "Crafters",
            Group.Gatherer => "Gatherers",
            _ => "Unknown",
        };

    public static readonly Group[] All =
    [
        Group.Tank, Group.Melee, Group.Ranged, Group.Caster,
        Group.Healer, Group.Crafter, Group.Gatherer,
    ];

    private static string RoleFolder(Configuration config, Group group)
        => group switch
        {
            Group.Tank => config.TankFolder,
            Group.Melee => config.MeleeFolder,
            Group.Ranged => config.RangedFolder,
            Group.Caster => config.CasterFolder,
            Group.Healer => config.HealerFolder,
            Group.Crafter => config.CrafterFolder,
            Group.Gatherer => config.GathererFolder,
            _ => string.Empty,
        };

    /// <summary>The coarser bucket a role sits in, for anyone who does not want five folders.</summary>
    private static string DisciplineFolder(Configuration config, Group group)
        => group switch
        {
            Group.Tank or Group.Melee or Group.Ranged => config.WarFolder,
            Group.Caster or Group.Healer => config.MagicFolder,
            Group.Crafter => config.CrafterFolder,
            Group.Gatherer => config.GathererFolder,
            _ => string.Empty,
        };

    /// <summary>
    /// Where to look for this group's designs, most specific first. Both a flat layout and a
    /// nested one work, so "tank" and "war/tank" are equally valid places to have put them,
    /// and a role folder that does not exist simply falls through to the discipline.
    /// </summary>
    public static IEnumerable<string> FoldersFor(Configuration config, string root, Group group)
    {
        var role = RoleFolder(config, group).Trim().Trim('/');
        var discipline = DisciplineFolder(config, group).Trim().Trim('/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     role,
                     Combine(discipline, role),
                     discipline,
                 })
        {
            var folder = candidate.Trim().Trim('/');
            if (folder.Length == 0 || !seen.Add(folder))
                continue;

            yield return Combine(root, folder);
        }
    }

    /// <summary>A design folder path, with an empty base meaning the root.</summary>
    public static string Combine(string baseFolder, string sub)
    {
        baseFolder = baseFolder.Trim().Trim('/');
        sub = sub.Trim().Trim('/');

        if (sub.Length == 0)
            return baseFolder;

        return baseFolder.Length == 0 ? sub : baseFolder + "/" + sub;
    }

    public static bool IsInFolder(string path, string folder)
    {
        if (folder.Length == 0)
            return true;

        return path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>In this folder itself rather than in one of its subfolders.</summary>
    public static bool IsDirectlyIn(string path, string folder)
    {
        if (!IsInFolder(path, folder))
            return false;

        var rest = folder.Length == 0 ? path : path[(folder.Length + 1)..];
        return !rest.Contains('/');
    }
}
