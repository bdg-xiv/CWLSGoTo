using Dalamud.Configuration;

namespace FateHopper;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Open the list on entering South Horn and close it on leaving.</summary>
    public bool AutoOpen = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
