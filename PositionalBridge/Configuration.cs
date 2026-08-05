using Dalamud.Configuration;

namespace PositionalBridge;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
