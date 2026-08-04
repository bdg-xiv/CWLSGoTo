using System.Collections.Generic;
using Dalamud.Configuration;

namespace MountHeels;

public record MountOffset
{
    /// <summary>
    /// Stand at the height the mods are giving - whatever Simple Heels already puts this
    /// character at on the ground. This is the whole point for a mount you stand on top of, and
    /// it keeps up with a change of shoes without anything being typed in here.
    /// </summary>
    public bool UseModelHeight { get; set; } = true;

    // Trim on top of that, for a mount whose platform is not quite where the game says it is.
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>Degrees, matching what Simple Heels' own command takes.</summary>
    public float Rotation { get; set; }

    public bool IsZero => X == 0f && Y == 0f && Z == 0f && Rotation == 0f;

    /// <summary>Nothing to say about this mount, so leave Simple Heels to it.</summary>
    public bool DoesNothing => !UseModelHeight && IsZero;
}

public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Offsets by mount id. A mount that is not in here is left entirely alone -
    /// Simple Heels keeps doing whatever it did before for it.</summary>
    public Dictionary<uint, MountOffset> Offsets { get; set; } = [];

    /// <summary>
    /// Version 1 handed its offsets to Simple Heels over IPC, which cannot move a mounted
    /// character, so whatever numbers are in there were dialled in against a preview that never
    /// moved and mean nothing. The mounts themselves are worth keeping; the numbers are not.
    /// </summary>
    public void Migrate()
    {
        if (Version >= CurrentVersion)
            return;

        foreach (var mount in new List<uint>(Offsets.Keys))
            Offsets[mount] = new MountOffset { UseModelHeight = true };

        Version = CurrentVersion;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
