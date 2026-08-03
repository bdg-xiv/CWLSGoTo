using System;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace GlamRoulette;

/// <summary>
/// Splits the design pool by what the wearer is. The four groupings are the game's own
/// ClassJobCategory rows rather than anything guessed from job names, so a job added in a
/// future patch lands in the right pool without this needing to know it exists.
/// </summary>
internal static class JobPools
{
    private const uint DisciplineOfWar = 30;
    private const uint DisciplineOfMagic = 31;
    private const uint DisciplineOfTheLand = 32;
    private const uint DisciplineOfTheHand = 33;

    public enum Group
    {
        Unknown,
        War,
        Magic,
        Crafter,
        Gatherer,
    }

    public static Group GroupOf(IPlayerCharacter player)
        => player.ClassJob.ValueNullable?.ClassJobCategory.RowId switch
        {
            DisciplineOfWar => Group.War,
            DisciplineOfMagic => Group.Magic,
            DisciplineOfTheHand => Group.Crafter,
            DisciplineOfTheLand => Group.Gatherer,
            _ => Group.Unknown,
        };

    public static string FolderFor(Configuration config, Group group)
        => group switch
        {
            Group.War => config.WarFolder,
            Group.Magic => config.MagicFolder,
            Group.Crafter => config.CrafterFolder,
            Group.Gatherer => config.GathererFolder,
            _ => string.Empty,
        };

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
