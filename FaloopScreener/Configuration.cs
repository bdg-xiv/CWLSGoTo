using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FaloopScreener;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Faloop account (required - window data is only served to logged-in accounts).
    // Stored in plain text in the plugin config, same as Faloop Integration.
    public string FaloopUsername { get; set; } = "";
    public string FaloopPassword { get; set; } = "";

    // Worlds shown in the table. Defaults to all of Crystal.
    public HashSet<string> EnabledWorlds { get; set; } = FaloopData.CrystalWorlds.ToHashSet();

    // Hunts / zones the user filtered out of the table.
    public HashSet<string> HiddenMobs { get; set; } = [];
    public HashSet<string> HiddenZones { get; set; } = [];

    // Attempt cap per leve-spawner run (each initiation costs one allowance).
    public int LeveAttempts { get; set; } = 5;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
