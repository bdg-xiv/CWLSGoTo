using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GatherTally;

[Serializable]
public class CachedProgress
{
    public uint Current { get; set; }
    public uint Max { get; set; }
    public DateTime RetrievedAt { get; set; }

    // The counter's value before its last change, so a row can show what was gained
    // since then. Only meaningful once a second reading has come in.
    public uint Previous { get; set; }
    public bool HasPrevious { get; set; }
    public DateTime ChangedAt { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Achievement progress per character (keyed by content id) - progress lives on the
    // server and is only fetched on refresh, so cache what we saw.
    public Dictionary<ulong, Dictionary<uint, CachedProgress>> ProgressByCharacter { get; set; } = [];

    // Whether each of the three sections is expanded, and whether the window itself was
    // open when the game closed.
    public Dictionary<string, bool> SectionOpen { get; set; } = [];
    public bool WindowOpen { get; set; } = false;

    public bool HideCompleted { get; set; } = false;
    public bool HideMeta { get; set; } = false;

    // Auto-refresh re-fetches the expanded sections on a timer, for watching counters
    // move while gathering.
    public bool AutoRefresh { get; set; } = false;
    public int AutoRefreshSeconds { get; set; } = 60;

    // Among items that advance an achievement equally, prefer the one that sells fastest
    // on the home world (looked up from Universalis).
    public bool PreferBestSelling { get; set; } = true;

    // Pause an auto-gather run when retainers come up, go home to the summoning bell, let
    // AutoRetainer work, then carry on gathering.
    public bool RetainerRunEnabled { get; set; } = false;

    // Turn GatherBuddy Reborn's auto-gather off once the achievement a list was built for
    // is earned. The achievement being watched survives a restart, since a long grind
    // easily outlasts one.
    public bool StopWhenAchieved { get; set; } = true;
    public uint WatchedAchievementId { get; set; }
    public string WatchedAchievementName { get; set; } = "";

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
