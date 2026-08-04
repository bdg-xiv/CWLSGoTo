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

    /// <summary>Draw from a different pool depending on what the wearer is: a subfolder per
    /// discipline, under the design folder.</summary>
    public bool MatchJobCategory { get; set; } = true;

    /// <summary>Designs sitting directly in the design folder rather than in one of the
    /// discipline subfolders are fair game for everyone.</summary>
    public bool IncludeSharedDesigns { get; set; } = true;

    // Per-role, checked first.
    public string TankFolder { get; set; } = "tank";
    public string MeleeFolder { get; set; } = "melee";
    public string RangedFolder { get; set; } = "ranged";
    public string CasterFolder { get; set; } = "caster";
    public string HealerFolder { get; set; } = "healer";
    public string CrafterFolder { get; set; } = "crafter";
    public string GathererFolder { get; set; } = "gatherer";

    // The coarser bucket, used when a role has no folder of its own.
    public string WarFolder { get; set; } = "war";
    public string MagicFolder { get; set; } = "magic";

    /// <summary>Re-dye each outfit - one colour per channel across the whole outfit - so two
    /// people in the same design still differ.</summary>
    public bool RandomizeDyes { get; set; } = true;

    /// <summary>Roll the second dye channel separately instead of matching the first.</summary>
    public bool DyeSecondChannel { get; set; } = true;

    /// <summary>Only draw from designs whose Glamourer folder path starts with this.
    /// Empty means every design, which is rarely what anyone wants.</summary>
    public string DesignFolder { get; set; } = string.Empty;

    /// <summary>
    /// Who is wearing what, keyed by "Name@World". This is the whole point of the plugin
    /// being stable rather than a slot machine, so it lives in the config rather than in
    /// memory - the same person looks the same tomorrow.
    /// </summary>
    public Dictionary<string, Guid> Assignments { get; set; } = [];

    /// <summary>When each player was last in front of us, keyed by name and world without the
    /// role, so being seen on any job keeps all of their outfits alive.</summary>
    public Dictionary<string, DateTime> LastSeen { get; set; } = [];

    /// <summary>Players whose outfits are kept no matter how long they have been gone.</summary>
    public HashSet<string> Pinned { get; set; } = [];

    /// <summary>How long an unseen player keeps their outfit. Zero means forever.</summary>
    public int RememberMinutes { get; set; } = 30;

    /// <summary>Re-apply periodically, since anything that redraws a character drops the
    /// design and Glamourer will not put it back on its own.</summary>
    public bool Reapply { get; set; } = true;

    public int ReapplySeconds { get; set; } = 30;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
