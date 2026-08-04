using Dalamud.Configuration;

namespace HuntSpots;

public class Configuration : IPluginConfiguration
{
    // Verified to exist rather than guessed at, and deliberately not 60355 or 60356: those are
    // Eureka Linker's, and two plugins on the same icon is confusing on a busy map.
    public const uint DefaultSIcon = 61234;
    public const uint DefaultAIcon = 61233;
    public const uint DefaultBIcon = 61221;

    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public bool ShowS { get; set; } = true;
    public bool ShowA { get; set; } = true;

    /// <summary>B ranks share most of the same spots and are rarely what anyone is looking
    /// for, so they stay off until asked for.</summary>
    public bool ShowB { get; set; }

    public uint SIcon { get; set; } = DefaultSIcon;
    public uint AIcon { get; set; } = DefaultAIcon;
    public uint BIcon { get; set; } = DefaultBIcon;

    public float IconSize { get; set; } = 28f;

    /// <summary>Only draw on the map of the zone you are standing in. Off means the points
    /// follow you around the map as you browse other zones.</summary>
    public bool CurrentZoneOnly { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
