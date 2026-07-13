using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace ModGuard;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool AutoMode { get; set; } = true;

    // Case-insensitive fragments matched against installed plugins' Name and
    // InternalName. Most sync plugins are Mare Synchronos forks, and several keep
    // "MareSynchronos" as their internal name, so that fragment is included too.
    public List<string> WatchTerms { get; set; } =
    [
        "lightless",
        "snowcloak",
        "sphene",
        "playersync",
        "maresynchronos",
    ];

    // The empty temporary collection currently force-assigned to the player, if any.
    // Persisted so the hidden state survives a plugin reload while Penumbra keeps
    // running; a game restart kills temporary collections, which is detected and
    // cleaned up on startup.
    public Guid? ActiveTempCollection { get; set; }

    // Whether the current hide was triggered by auto mode (only auto-hides are
    // auto-restored; a manual hide stays until manually restored).
    public bool WasAutoHidden { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
