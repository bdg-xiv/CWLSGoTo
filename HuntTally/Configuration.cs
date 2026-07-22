using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace HuntTally;

[Serializable]
public class CachedProgress
{
    public uint Current { get; set; }
    public uint Max { get; set; }
    public DateTime RetrievedAt { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Achievement progress per character (keyed by content id) - progress lives on
    // the server and is only fetched on refresh, so cache what we saw.
    public Dictionary<ulong, Dictionary<uint, CachedProgress>> ProgressByCharacter { get; set; } = [];

    // Unique elite marks (NotoriousMonster row ids) the plugin has seen die near the
    // player, per character. The server only reports totals for the "slay each unique
    // mark" achievements, so which specific marks are done is tracked on our side.
    public Dictionary<ulong, HashSet<uint>> SlainMarksByCharacter { get; set; } = [];

    // Hide achievements that are already complete.
    public bool HideCompleted { get; set; } = false;

    // Hide meta achievements whose requirement is completing other achievements.
    public bool HideMetaAchievements { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
