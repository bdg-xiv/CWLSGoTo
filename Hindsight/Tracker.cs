using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Hindsight;

/// <summary>
/// Rebuilds cooldown state from the action uses a duty replay feeds back through the
/// game's packet handlers. Every timestamp in here is replay-position seconds, never
/// wall clock, so pausing, playback speed, and chapter skips keep the timers honest.
/// </summary>
public sealed class Tracker
{
    /// <summary>One recast group of one player - a 120s burst, mudra charges, Sprint.
    /// Named and drawn as the action most recently seen from that group.</summary>
    public sealed class Cooldown
    {
        public string Name = "";
        public ushort Icon;
        public float RechargeSeconds;
        public int MaxCharges = 1;
        public float ChargesAtLastUse;
        public float LastUseSeconds;

        public float ChargesAt(float now) => RechargeSeconds <= 0f
            ? MaxCharges
            : Math.Min(MaxCharges, ChargesAtLastUse + Math.Max(0f, now - LastUseSeconds) / RechargeSeconds);

        /// <summary>When every charge was (or will be) back.</summary>
        public float FullAtSeconds => LastUseSeconds + (MaxCharges - ChargesAtLastUse) * RechargeSeconds;
    }

    public sealed class Player
    {
        public string Name = "";
        public byte Job;
        public readonly Dictionary<byte, Cooldown> Groups = [];
    }

    /// <summary>Everyone seen using a trackable action this playback, by entity id.</summary>
    public readonly Dictionary<uint, Player> Players = [];

    private readonly Dictionary<uint, (byte Group, float Recharge, int Charges, string Name, ushort Icon)?> knownActions = [];
    private byte gcdGroup;

    /// <summary>The recast group ordinary GCD skills share, found by majority vote among
    /// 2.5s player actions so a sheet renumbering can't silently break the filter.</summary>
    private byte GcdGroup => gcdGroup != 0
        ? gcdGroup
        : gcdGroup = Svc.Data.GetExcelSheet<LuminaAction>()
            .Where(a => a.IsPlayerAction && a.Recast100ms == 25 && a.CooldownGroup != 0)
            .GroupBy(a => a.CooldownGroup)
            .OrderByDescending(g => g.Count())
            .First().Key;

    public void NoteUse(uint entityId, string name, byte job, uint actionId, float now, float minRecast)
    {
        if (Resolve(actionId) is not { } action || action.Recharge < minRecast)
            return;

        if (!Players.TryGetValue(entityId, out var player))
            Players[entityId] = player = new Player();
        if (player.Job != job)
        {
            player.Groups.Clear();
            player.Job = job;
        }
        player.Name = name;

        if (!player.Groups.TryGetValue(action.Group, out var cd))
            player.Groups[action.Group] = cd = new Cooldown
            {
                MaxCharges = action.Charges,
                // First sight: assume they were sitting on full charges until now.
                ChargesAtLastUse = action.Charges,
            };

        var had = cd.ChargesAt(now);
        cd.ChargesAtLastUse = Math.Max(0f, had - 1f);
        cd.LastUseSeconds = now;
        cd.Name = action.Name;
        cd.Icon = action.Icon;
        cd.RechargeSeconds = action.Recharge;
        cd.MaxCharges = action.Charges;
    }

    public void Forget() => Players.Clear();

    /// <summary>An action's recast group, or null for pure-GCD skills and anything else
    /// without a cooldown of its own. First sight of an id reads the sheet; every later
    /// sight is a dictionary hit, nulls included.</summary>
    private (byte Group, float Recharge, int Charges, string Name, ushort Icon)? Resolve(uint actionId)
    {
        if (knownActions.TryGetValue(actionId, out var cached))
            return cached;

        (byte Group, float Recharge, int Charges, string Name, ushort Icon)? info = null;
        if (Svc.Data.GetExcelSheet<LuminaAction>().TryGetRow(actionId, out var row))
        {
            var group = row.CooldownGroup;
            if (group == 0 || group == GcdGroup)
                group = row.AdditionalCooldownGroup;
            if (group != 0 && group != GcdGroup)
                info = (group, row.Recast100ms / 10f, Math.Max(row.MaxCharges, (byte)1), row.Name.ExtractText(), row.Icon);
        }

        knownActions[actionId] = info;
        return info;
    }
}
