using Dalamud.Configuration;
using System;

namespace Hindsight;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Open the window when replay playback starts, close it when it ends.</summary>
    public bool AutoOpen { get; set; } = true;

    /// <summary>Actions that recharge faster than this never get a row, keeping short
    /// utility churn out of a review window.</summary>
    public float MinRecastSeconds { get; set; } = 10f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
