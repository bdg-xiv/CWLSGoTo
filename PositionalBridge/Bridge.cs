using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace PositionalBridge;

/// <summary>
/// Tells Avarice which action Wrath is about to use, so its positional cone points where the
/// button actually goes.
///
/// Avarice can already follow a rotation plugin, but on one channel only - an event named for
/// Rotation Solver Reborn - and Wrath publishes nothing of the kind. Its IPC is leases, combo
/// config and an OnActionUsed that fires afterwards; there is no "what is next".
///
/// It does not need to publish one. Wrath works by hooking the game's adjusted-action lookup,
/// which is why the button on your bar turns into the next step of the combo. Asking the game
/// that same question goes straight through Wrath's hook and comes back with the action it
/// has chosen - not a guess at it, the same value the button is about to fire.
/// </summary>
internal sealed class Bridge
{
    /// <summary>The event Avarice listens on. Sending costs nothing if it is not installed:
    /// with no subscribers this goes nowhere.</summary>
    private const string NextAction = "RotationSolverReborn.ActionUpdater.NextActionChanged";

    /// <summary>
    /// The button Wrath replaces for single target, per job - taken from Wrath's own combos
    /// rather than worked out, so it is the action it really does hook. Every one is the level
    /// one weaponskill the whole combo hangs off, which is why the base classes are here too:
    /// three of these belong to Pugilist, Lancer and Rogue rather than to the job.
    /// </summary>
    private static readonly Dictionary<uint, uint> Roots = new()
    {
        [2] = 53,       // Pugilist  - Bootshine
        [20] = 53,      // Monk
        [4] = 75,       // Lancer    - True Thrust
        [22] = 75,      // Dragoon
        [29] = 2240,    // Rogue     - Spinning Edge
        [30] = 2240,    // Ninja
        [34] = 7477,    // Samurai   - Hakaze
        [39] = 24373,   // Reaper    - Slice
        [41] = 34606,   // Viper     - Steel Fangs
    };

    private readonly ICallGateProvider<uint, object?> channel =
        Svc.PluginInterface.GetIpcProvider<uint, object?>(NextAction);

    private uint lastSent;
    private DateTime sentAt = DateTime.MinValue;

    public uint Job { get; private set; }
    public uint Root { get; private set; }
    public uint Resolved { get; private set; }
    public uint Sent => lastSent;

    public bool JobSupported => Root != 0;

    /// <summary>Rotation Solver being installed too would mean two plugins talking over each
    /// other on the same channel. Worth saying rather than silently fighting.</summary>
    public static bool RotationSolverPresent
    {
        get
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
                if (plugin.IsLoaded && plugin.InternalName.StartsWith("RotationSolver", StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }

    public unsafe void Update(Configuration config)
    {
        Job = Svc.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        Root = Roots.TryGetValue(Job, out var root) ? root : 0;
        Resolved = 0;

        if (!config.Enabled || Root == 0)
            return;

        var actions = ActionManager.Instance();
        if (actions == null)
            return;

        var resolved = actions->GetAdjustedActionId(Root);
        Resolved = resolved;

        // Weave windows resolve to an off-global ability, and forwarding those would blink the
        // cone off between every pair of weaponskills. The last weaponskill is the one the
        // positional belongs to, so anything off the global cooldown is simply not news.
        if (resolved == 0 || !IsGlobal(resolved))
            resolved = lastSent;

        if (resolved == 0)
            return;

        // Avarice drops anything it has not heard about in five seconds, so a standing answer
        // still has to be repeated.
        if (resolved == lastSent && DateTime.UtcNow - sentAt < TimeSpan.FromSeconds(2))
            return;

        try
        {
            channel.SendMessage(resolved);
            lastSent = resolved;
            sentAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[PositionalBridge] Could not hand {resolved} over: {ex.Message}");
        }
    }

    /// <summary>Forget what we last said, for when the job changes or this is switched off -
    /// otherwise a Viper's positional would be repeated at a Dragoon.</summary>
    public void Reset()
    {
        lastSent = 0;
        sentAt = DateTime.MinValue;
    }

    public static string NameOf(uint actionId)
    {
        if (actionId == 0)
            return "-";

        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        return sheet.TryGetRow(actionId, out var action) && action.Name.ExtractText().Length > 0
            ? $"{action.Name.ExtractText()} ({actionId})"
            : $"#{actionId}";
    }

    /// <summary>A weaponskill or a spell, as opposed to an ability - the categories the game
    /// puts on the global cooldown.</summary>
    private static bool IsGlobal(uint actionId)
    {
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        return sheet.TryGetRow(actionId, out var action) && action.ActionCategory.RowId is 2 or 3;
    }
}
