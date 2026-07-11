using Dalamud.Configuration;
using System;

namespace LogoutOnTell;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Off by default: an auto-logout firing unexpectedly right after install would be
    // a nasty surprise. The user arms it deliberately with /logoutontell.
    public bool Enabled { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
