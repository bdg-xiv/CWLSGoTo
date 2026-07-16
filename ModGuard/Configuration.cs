using Dalamud.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace ModGuard;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool AutoMode { get; set; } = true;

    // Whether "restore" should unload the detected sync plugins first. Off by default:
    // Mare-style plugins render the character continuously and being unloaded mid-draw
    // leaves the character black. Hiding still keeps mods private without unloading.
    public bool UnloadSyncOnRestore { get; set; } = false;

    // Also revert the character's Glamourer state while hiding, since sync plugins
    // share glamours/customizations too, not just Penumbra mods.
    public bool IncludeGlamourer { get; set; } = true;

    // The character's Glamourer state captured right before reverting it, so it can
    // be reapplied exactly on restore. Only set while hidden.
    public string? SavedGlamourerState { get; set; }

    // Case-insensitive fragments matched against installed plugins' Name and
    // InternalName. Most sync plugins are Mare Synchronos forks, and several keep
    // "MareSynchronos" as their internal name, so that fragment is included too.
    // Replace on deserialize: without it Newtonsoft appends the saved terms to the
    // defaults below, duplicating the list on every load.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
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

    // Sync plugins that were unloaded by the restore action, so the next hide can
    // re-enable them.
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> UnloadedSyncPlugins { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
