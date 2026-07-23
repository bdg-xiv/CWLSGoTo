using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace AutoLister;

/// <summary>What one retainer had listed the last time their sell list was open.
/// Other retainers' markets can't be read while a different one is summoned, so
/// these snapshots are how Auto List knows an item is already for sale elsewhere.</summary>
[Serializable]
public class RetainerSnapshot
{
    public string Name { get; set; } = "";
    public DateTime At { get; set; }
    public List<uint> Items { get; set; } = [];
    public List<uint> HqItems { get; set; } = [];
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public Dictionary<ulong, RetainerSnapshot> RetainerListings { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
