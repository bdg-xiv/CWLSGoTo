using Dalamud.Configuration;
using System;

namespace StackSplitter;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public const int DefaultStackSize = 50;
    public const int MinStackSize = 1;
    public const int MaxStackSize = 999;

    /// <summary>Stacks are split until none holds more than this many units.</summary>
    public int StackSize { get; set; } = DefaultStackSize;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
