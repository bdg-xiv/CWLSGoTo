using Dalamud.Configuration;
using System;

namespace DesynthAllCommand;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Only desynthesize items that still grant desynthesis skill - the ones
    // SimpleTweaks' Extended Desynthesis Window colors yellow/red. Green items
    // (current skill >= item level + 50, or skill already at the game-wide cap)
    // are left alone.
    public bool OnlySkillGain { get; set; } = true;

    // Never desynthesize an item that is part of any gear set.
    public bool SkipGearsetItems { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
