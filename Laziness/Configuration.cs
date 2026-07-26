using Dalamud.Configuration;
using System;

namespace Laziness;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Whether the window was open last session, so it comes back the same way.</summary>
    public bool WindowOpen { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
