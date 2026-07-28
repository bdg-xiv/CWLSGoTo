using Dalamud.Configuration;

namespace ArtisanGatherBridge;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Write the amount still missing rather than the recipe's full requirement.
    /// Falls back to the requirement when nothing is missing.</summary>
    public bool UseRemaining = true;

    /// <summary>Switch the auto-gather list on as soon as something lands in it. Without
    /// this the list is created but sits there inert until it is ticked in GatherBuddy
    /// Reborn.</summary>
    public bool EnableList = true;

    /// <summary>Also flip GatherBuddy Reborn's auto-gather switch, so it leaves for the
    /// node immediately. Off by default: adding ingredients usually happens while a list
    /// is still being put together.</summary>
    public bool StartAutoGather;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
