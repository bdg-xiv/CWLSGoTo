using Dalamud.Configuration;
using Dalamud.Game.Text;
using System;
using System.Collections.Generic;

namespace SamplePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = CurrentVersion;

    // v1: added Echo to existing configs so plugin-printed hunt reports
    // (e.g. Faloop Integration, which prints to Echo) get [Go To] links.
    public const int CurrentVersion = 1;

    // Preserve the plugin's original behavior (CWLS1/CWLS2 only) as the default for new installs.
    public HashSet<XivChatType> WatchedChannels { get; set; } =
    [
        XivChatType.CrossLinkShell1,
        XivChatType.CrossLinkShell2,
        XivChatType.Echo,
    ];

    /// <summary>Applies one-time migrations to configs saved by older versions. Returns true if changed.</summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 1)
            WatchedChannels.Add(XivChatType.Echo);

        Version = CurrentVersion;
        return true;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
