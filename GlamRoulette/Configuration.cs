using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace GlamRoulette;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Leave party members in the glamour they chose.</summary>
    public bool SkipParty { get; set; }

    /// <summary>Only dress female characters, since the designs are cut for them.</summary>
    public bool FemaleOnly { get; set; } = true;

    /// <summary>Only draw from designs whose Glamourer folder path starts with this.
    /// Empty means every design, which is rarely what anyone wants.</summary>
    public string DesignFolder { get; set; } = string.Empty;

    /// <summary>
    /// Who is wearing what, keyed by "Name@World". This is the whole point of the plugin
    /// being stable rather than a slot machine, so it lives in the config rather than in
    /// memory - the same person looks the same tomorrow.
    /// </summary>
    public Dictionary<string, Guid> Assignments { get; set; } = [];

    /// <summary>Re-apply periodically, since anything that redraws a character drops the
    /// design and Glamourer will not put it back on its own.</summary>
    public bool Reapply { get; set; } = true;

    public int ReapplySeconds { get; set; } = 30;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
