using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

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

    // Expansions shown in the table. Defaults to all of them.
    public HashSet<string> EnabledExpansions { get; set; } =
        ["a_realm_reborn", "heavensward", "stormblood", "shadowbringers", "endwalker", "dawntrail"];

    // Hunts / zones the user filtered out of the table.
    public HashSet<string> HiddenMobs { get; set; } = [];
    public HashSet<string> HiddenZones { get; set; } = [];

    // Attempt cap per leve-spawner run (each initiation costs one allowance).
    public int LeveAttempts { get; set; } = 5;

    // Sonar detection ring: circle around the player on the game's map / minimap
    // showing how far the client streams in marks (what Sonar can pick up).
    public bool SonarRingOnMap { get; set; } = true;
    public bool SonarRingOnMinimap { get; set; } = true;
    public int SonarRingRadius { get; set; } = 100;
    public Vector4 SonarRingColor { get; set; } = new(0.25f, 0.75f, 1f, 0.9f);
    public float SonarRingThickness { get; set; } = 2f;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
