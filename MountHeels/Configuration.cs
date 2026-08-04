using System.Collections.Generic;
using Dalamud.Configuration;

namespace MountHeels;

public record MountOffset
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }

    public bool IsZero => X == 0f && Y == 0f && Z == 0f && Rotation == 0f;
}

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Offsets by mount id. A mount that is not in here is left entirely alone -
    /// Simple Heels keeps doing whatever it did before for it.</summary>
    public Dictionary<uint, MountOffset> Offsets { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
