using Dalamud.Configuration;
using System;

namespace ModSnap;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool ContextMenuEnabled { get; set; } = true;

    // Only offer the context menu entry on actual players. NPCs and event actors can
    // technically be snapshotted too (Penumbra supports it), but the entry on every
    // NPC menu is mostly noise.
    public bool PlayersOnly { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
