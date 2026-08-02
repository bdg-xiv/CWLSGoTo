using System.Collections.Generic;
using System.Numerics;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina.Excel.Sheets;

namespace OccultCoffers;

internal enum CofferKind
{
    Silver,
    Bronze,
}

/// <summary>One place a coffer can appear, straight out of the zone's layout data.</summary>
internal sealed class CofferSpot
{
    public required ulong Key;
    public required Vector3 World;
    public required CofferKind Kind;
    public required uint MapId;

    /// <summary>The player has been close enough to see whether anything is here.</summary>
    public bool Checked;

    /// <summary>
    /// A coffer is known to have been here - seen in the world, or reported by another
    /// plugin. Sticky, because the arithmetic cares that the coffer was accounted for, not
    /// whether it is still sitting there.
    /// </summary>
    public bool HadCoffer;

    /// <summary>Where that knowledge came from, so the tooltip can say so.</summary>
    public bool ReportedCoffer;

    /// <summary>
    /// We are close enough to see the spot and there is nothing on it. This is what turns a
    /// confirmed coffer back into a swept one the moment it gets looted.
    /// </summary>
    public bool CofferGone;

    /// <summary>Known to hold a coffer, and it is still there.</summary>
    public bool HoldsCoffer => HadCoffer && !CofferGone;
}

internal static class CofferSpots
{
    // The two SharedGroup files the game uses for the Occult Crescent coffers. This is the
    // only thing that tells silver and bronze apart before one has spawned.
    private const uint BronzeSgb = 1596;
    private const uint SilverSgb = 1597;

    // The Treasure sheet row id sits here on every layout instance of type Treasure.
    private const int TreasureRowIdOffset = 48;

    public static unsafe List<CofferSpot> Read(Zones.ZoneInfo zone, float subterraneCeilingY)
    {
        var spots = new List<CofferSpot>();

        var layout = LayoutWorld.Instance()->ActiveLayout;
        if (layout == null)
            return spots;

        if (!layout->InstancesByType.TryGetValue(InstanceType.Treasure, out var byId, false) || byId.Value == null)
            return spots;

        var treasures = Svc.Data.GetExcelSheet<Treasure>();
        if (treasures == null)
            return spots;

        foreach (var entry in byId.Value->Values)
        {
            var instance = entry.Value;
            if (instance == null)
                continue;

            var rowId = *(uint*)((byte*)instance + TreasureRowIdOffset);
            if (!treasures.TryGetRow(rowId, out var treasure))
                continue;

            CofferKind kind;
            switch (treasure.SGB.RowId)
            {
                case BronzeSgb: kind = CofferKind.Bronze; break;
                case SilverSgb: kind = CofferKind.Silver; break;
                default: continue;
            }

            var position = *instance->GetTranslationImpl();
            var id = instance->Id;

            spots.Add(new CofferSpot
            {
                Key = ((ulong)id.LayerKey << 32) | id.InstanceKey,
                World = position,
                Kind = kind,
                MapId = zone.MapIdFor(position, subterraneCeilingY),
            });
        }

        return spots;
    }
}
