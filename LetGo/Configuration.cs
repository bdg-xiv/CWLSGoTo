using Dalamud.Configuration;

namespace LetGo;

public enum Modifier
{
    Shift,
    Ctrl,
    Alt,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled = true;

    /// <summary>Shift by default, but it is also a common hotbar modifier - Ctrl and Alt
    /// are there for anyone whose keybinds would fight over it.</summary>
    public Modifier Key = Modifier.Shift;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
