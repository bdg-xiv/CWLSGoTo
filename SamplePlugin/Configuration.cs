using Dalamud.Configuration;
using Dalamud.Game.Text;
using System;
using System.Collections.Generic;

namespace SamplePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Preserve the plugin's original behavior (CWLS1/CWLS2 only) as the default for new installs.
    public HashSet<XivChatType> WatchedChannels { get; set; } =
    [
        XivChatType.CrossLinkShell1,
        XivChatType.CrossLinkShell2,
    ];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
