using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace GatherTally;

/// <summary>Interrupts an auto-gather run when retainers come up, takes the trip home to
/// the summoning bell, lets AutoRetainer work, and puts gathering back on afterwards.
///
/// Everything here goes through published IPC - GatherBuddy Reborn's auto-gather toggle,
/// Lifestream's "home" command, and AutoRetainer's own single-character multi mode, which
/// is the part that walks to the bell and handles the retainers. Every stage has a
/// timeout, because a stuck trip that never resumes gathering is worse than a trip that
/// gives up and says so.</summary>
public sealed class RetainerRun
{
    private enum Stage
    {
        Idle,
        GoingHome,
        WaitingForTravel,
        ReachingBell,
        WaitingForRetainers,
        Done,
    }

    // Lifestream's trip home is a teleport plus a walk; retainers are a few menus each.
    private const int TravelTimeoutMs = 180_000;
    private const int RetainerTimeoutMs = 600_000;

    // AutoRetainer needs a moment to pick the work up before its busy flag means anything.
    private const int StartGraceMs = 5_000;

    // Lifestream drops you inside the house; the bell is usually a few steps away, so
    // keep trying while the character settles.
    private const int BellTimeoutMs = 120_000;
    private const int BellPokeIntervalMs = 1_000;
    private const int IdleGraceMs = 15_000;

    // Try the bell where we stand first - an inn or a plot that drops you inside needs no
    // entering - and only ask to go in if nothing answers.
    private const int HouseEntryDelayMs = 4_000;

    // How long AutoRetainer has to stay quiet before it counts as finished. Post-venture
    // work carries on in gaps of several seconds after the last retainer is handled.
    private const int IdleSettleMs = 12_000;

    private readonly Configuration config;

    private Stage stage = Stage.Idle;
    private long stageSince;
    private bool resumeGathering;
    private bool sawAutoRetainerWork;
    private long lastBellPokeAt;
    private bool houseEntryRequested;
    private bool houseEntryFailed;
    private long lastBusyAt;
    private bool? schedulerWasEnabled;

    public RetainerRun(Configuration config) => this.config = config;

    public bool Running => stage is not (Stage.Idle or Stage.Done);
    public string Status { get; private set; } = "";

    /// <summary>Stops a trip in flight from switching gathering back on - used when the
    /// achievement being gathered for finishes mid-trip.</summary>
    public void CancelResume() => resumeGathering = false;

    private string explanation = "";
    private long explainedAt;

    /// <summary>Why nothing is happening. Each preconditon fails silently on its own -
    /// a missing plugin and a plugin answering "no" look identical from an IPC call - so
    /// say which one is holding things up rather than leaving it a mystery. Throttled,
    /// since probing absent IPC throws.</summary>
    public string Explain()
    {
        if (Running)
            return Status;

        var now = Environment.TickCount64;
        if (now - explainedAt < 1000)
            return explanation;
        explainedAt = now;

        return explanation = Probe();
    }

    /// <summary>True while a node is open or being worked. The gathering window stays up
    /// between swings, so the condition flag alone would still let a trip start between
    /// two items of the same node.</summary>
    private static unsafe bool MidGather()
    {
        if (Svc.Condition[ConditionFlag.Gathering] || Svc.Condition[ConditionFlag.ExecutingGatheringAction])
            return true;

        foreach (var addon in (string[])["Gathering", "GatheringMasterpiece"])
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(addon, out var unit)
                && GenericHelpers.IsAddonReady(unit))
                return true;
        }

        return false;
    }

    private static string Probe()
    {
        if (!AutoRetainerIpc.Available)
            return "AutoRetainer not found";
        if (!LifestreamIpc.Available)
            return "Lifestream not found";

        var gathering = GatherBuddyIpc.IsAutoGatherEnabled();
        if (gathering == null)
            return "GatherBuddy Reborn not found";
        if (gathering == false)
            return "waiting - auto-gather is off";

        var ready = RetainerVentures.AnyComplete();
        if (ready == null)
            return "waiting - retainer list not loaded yet";
        if (ready == false)
            return "gathering - no venture finished yet";
        if (MidGather())
            return "venture done - finishing this node first";

        return "venture done - starting the trip";
    }

    public void Update()
    {
        if (!config.RetainerRunEnabled)
        {
            if (Running)
                Finish("Retainer trips turned off mid-run; gathering left as it is.", resume: false);
            return;
        }

        if (!Svc.ClientState.IsLoggedIn)
        {
            stage = Stage.Idle;
            return;
        }

        if (Running)
        {
            Advance();
            return;
        }

        // Only interrupt something that is actually running - this is a detour from
        // gathering, not a way to start retainers on its own.
        if (GatherBuddyIpc.IsAutoGatherEnabled() != true || RetainerVentures.AnyComplete() != true)
            return;

        // A venture does not stop being done in thirty seconds. Leaving mid-node throws
        // away the node, so let the current one finish first.
        if (MidGather())
            return;

        Begin();
    }

    private void Begin()
    {
        if (!LifestreamIpc.Available)
        {
            Report("Retainers are ready, but Lifestream isn't loaded to make the trip home.");
            config.RetainerRunEnabled = false;
            config.Save();
            return;
        }

        resumeGathering = true;

        // Note how AutoRetainer was set before touching it, so the trip can put it back.
        schedulerWasEnabled = AutoRetainerIpc.SchedulerEnabled();

        GatherBuddyIpc.SetAutoGatherEnabled(false);
        Report("A venture is done - pausing the gather run and heading home.");

        // Earlier versions started multi mode to do this. If it is still on it will fight
        // over where the character should be, and it was never wanted here.
        if (AutoRetainerIpc.MultiModeRunning() == true)
            Report("AutoRetainer's multi mode is on - turn it off, this doesn't use it any more.");

        Enter(Stage.GoingHome);
    }

    private void Advance()
    {
        var elapsed = Environment.TickCount64 - stageSince;

        switch (stage)
        {
            case Stage.GoingHome:
                LifestreamIpc.ExecuteCommand("home");
                Status = "Travelling home...";
                Enter(Stage.WaitingForTravel);
                break;

            case Stage.WaitingForTravel:
                if (elapsed > TravelTimeoutMs)
                {
                    Finish("Gave up waiting for the trip home; resuming gathering.", resume: true);
                    break;
                }

                // The command is queued, so give Lifestream a moment to look busy before
                // treating "not busy" as "arrived".
                if (elapsed > StartGraceMs && !LifestreamIpc.IsBusy())
                {
                    lastBellPokeAt = 0;
                    houseEntryRequested = false;
                    houseEntryFailed = false;

                    // Arm AutoRetainer before the bell opens. Its scheduler only picks
                    // retainers up while its own enable toggle is on, which is why the
                    // list opening by itself did nothing.
                    if (schedulerWasEnabled != true)
                        AutoRetainerIpc.EnableScheduler();
                    Enter(Stage.ReachingBell);
                }

                break;

            case Stage.ReachingBell:
                // Opening the bell is all AutoRetainer needs; its own scheduler takes
                // the retainers from there.
                if (SummoningBell.RetainerListOpen())
                {
                    sawAutoRetainerWork = false;
                    lastBusyAt = Environment.TickCount64;
                    Status = "AutoRetainer is working...";
                    Enter(Stage.WaitingForRetainers);
                    break;
                }

                if (houseEntryFailed)
                {
                    Finish("Couldn't get into the house; resuming gathering.", resume: true);
                    break;
                }

                if (elapsed > BellTimeoutMs)
                {
                    Finish("Couldn't reach a summoning bell here; resuming gathering.", resume: true);
                    break;
                }

                // Lifestream drops you at the plot, not inside it. Rather than path to the
                // door, hand that to AutoRetainer's own housing-entrance task - the same
                // one multi mode was using to get in before.
                if (!houseEntryRequested && elapsed > HouseEntryDelayMs)
                {
                    houseEntryRequested = true;
                    Status = "Entering the house...";
                    if (!AutoRetainerIpc.EnterHouse(() => houseEntryFailed = true))
                        houseEntryFailed = true;
                    break;
                }

                if (houseEntryRequested && AutoRetainerIpc.IsBusy() == true)
                {
                    Status = "Entering the house...";
                    break;
                }

                Status = "Looking for the summoning bell...";
                if (Environment.TickCount64 - lastBellPokeAt > BellPokeIntervalMs)
                {
                    lastBellPokeAt = Environment.TickCount64;
                    SummoningBell.TryInteract();
                }

                break;

            case Stage.WaitingForRetainers:
                if (elapsed > RetainerTimeoutMs)
                {
                    Finish("AutoRetainer took too long; resuming gathering.", resume: true);
                    break;
                }

                if (AutoRetainerIpc.IsBusy() == true)
                {
                    sawAutoRetainerWork = true;
                    lastBusyAt = Environment.TickCount64;
                    break;
                }

                // Busy goes false in the gaps between retainers and while post-venture
                // work runs, so one quiet reading means nothing - wait for it to stay
                // quiet. If it never stirred at all, it is not switched on.
                if (sawAutoRetainerWork)
                {
                    if (Environment.TickCount64 - lastBusyAt > IdleSettleMs)
                        Finish("Retainers done - back to gathering.", resume: resumeGathering);
                }
                else if (elapsed > IdleGraceMs)
                {
                    Finish("The bell is open but AutoRetainer isn't running - enable it and it will take over.",
                        resume: false);
                }

                break;
        }
    }

    private void Enter(Stage next)
    {
        stage = next;
        stageSince = Environment.TickCount64;
    }

    private void Finish(string message, bool resume)
    {
        stage = Stage.Idle;
        Status = "";

        // AutoRetainer leaves the retainer list up when it finishes, and gathering cannot
        // start again with a window in the way.
        SummoningBell.CloseRetainerList();

        // Put AutoRetainer back the way it was found. Only when it is known to have been
        // off - an unreadable state is left alone rather than guessed at.
        if (schedulerWasEnabled == false)
            AutoRetainerIpc.DisableScheduler();
        schedulerWasEnabled = null;
        if (resume)
            GatherBuddyIpc.SetAutoGatherEnabled(true);
        Report(message);
    }

    private static void Report(string message) => Svc.Chat.Print($"[Gather Tally] {message}");
}

internal static class GatherBuddyIpc
{
    /// <summary>Null when GatherBuddy Reborn isn't there to ask - which is a different
    /// thing from it answering "no".</summary>
    public static bool? IsAutoGatherEnabled() => Call<bool>("GatherBuddyReborn.IsAutoGatherEnabled");

    public static void SetAutoGatherEnabled(bool enabled)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled")
                .InvokeAction(enabled);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"GatherBuddy Reborn auto-gather toggle failed: {ex.Message}");
        }
    }

    private static T? Call<T>(string name) where T : struct
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Finds and opens a summoning bell.
///
/// Bells are matched by object name rather than data id, which is how AutoRetainer does it
/// too - housing bells, inn bells and the city ones are separate objects that share a
/// name. Opening the bell is the whole job here: AutoRetainer's own scheduler picks the
/// retainers up once the list is on screen.</summary>
internal static class SummoningBell
{
    private static readonly string[] Names = ["summoning bell"];

    // Housing bells sit further from their interaction point than city ones.
    private const float InteractRange = 6f;

    public static unsafe bool TryInteract()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return false;

        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
                continue;
            if (!Names.Contains(obj.Name.TextValue.ToLowerInvariant()))
                continue;
            if (Vector3.Distance(player.Position, obj.Position) > InteractRange)
                continue;

            TargetSystem.Instance()->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
            return true;
        }

        return false;
    }

    public static unsafe bool RetainerListOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
           && GenericHelpers.IsAddonReady(addon);

    /// <summary>Dismisses the retainer list. Sends the addon's own cancel first, which is
    /// what the Escape key does, and only then closes it outright - a bare Close can leave
    /// the game believing the bell is still being used.</summary>
    public static unsafe void CloseRetainerList()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
            return;

        try
        {
            Callback.Fire(addon, true, -1);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Closing the retainer list failed: {ex.Message}");
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var still)
            && GenericHelpers.IsAddonReady(still))
            still->Close(true);
    }
}

/// <summary>Whether any retainer has a venture waiting to be collected, read straight from
/// the game's own retainer list.
///
/// This deliberately does not ask AutoRetainer. Its
/// AreAnyRetainersAvailableForCurrentChara means "does AutoRetainer consider this character
/// actionable", which additionally requires offline data recorded for the character and
/// retainers enabled for it in AutoRetainer's config - conditions that have nothing to do
/// with whether a venture is done.</summary>
internal static class RetainerVentures
{
    /// <summary>True when a venture has come back, false when none has, null while the
    /// game has not populated the retainer list yet.</summary>
    public static unsafe bool? AnyComplete()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return null;

        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->VentureId == 0 || retainer->VentureComplete == 0)
                continue;
            if (retainer->VentureComplete <= now)
                return true;
        }

        return false;
    }
}

internal static class AutoRetainerIpc
{
    public static bool Available => IsBusy() != null;

    public static bool? IsBusy() => Call<bool>("AutoRetainer.PluginState.IsBusy");

    /// <summary>Whether multi mode is on. Nothing here switches it on any more - it is
    /// AutoRetainer's whole cross-character system, including relogging between alts and
    /// AFK switching, which is far more than a trip to one's own bell needs.</summary>
    public static bool? MultiModeRunning() => Call<bool>("AutoRetainer.PluginState.GetMultiModeStatus");

    /// <summary>Whether AutoRetainer's enable toggle is currently on, or null if that can't
    /// be read. Its IPC exposes no getter for this, so the scheduler's own flag is read
    /// directly; a null answer means the state is unknown and must be left alone rather
    /// than guessed at.</summary>
    public static bool? SchedulerEnabled()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "AutoRetainer");
            var field = assembly?.GetType("AutoRetainer.Scheduler.SchedulerMain")
                ?.GetField("PluginEnabledInternal",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(null) as bool?;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Reading AutoRetainer's enabled state failed: {ex.Message}");
            return null;
        }
    }

    public static void DisableScheduler()
    {
        try
        {
            Svc.Commands.ProcessCommand("/autoretainer d");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Disabling AutoRetainer failed: {ex.Message}");
        }
    }

    /// <summary>Ticks AutoRetainer's own enable toggle. Its scheduler only acts on an open
    /// retainer list while this is on, which is the difference between opening the bell by
    /// hand - where the user has already switched it on - and opening it from here. There
    /// is no IPC for it, but the command is a supported entry point; AutoRetainer dispatches
    /// its own commands this way too.</summary>
    public static void EnableScheduler()
    {
        try
        {
            Svc.Commands.ProcessCommand("/autoretainer e");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Enabling AutoRetainer failed: {ex.Message}");
        }
    }

    /// <summary>Queues AutoRetainer's housing-entrance task, which walks to the plot's
    /// entrance and goes inside. This is the piece multi mode used to contribute, and it
    /// already knows about private, shared, free company and apartment entrances - worth
    /// far more than pathing to a door by hand.</summary>
    public static bool EnterHouse(Action onFailure)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<Action, object>("AutoRetainer.PluginState.EnqueueHET")
                .InvokeAction(onFailure);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"AutoRetainer house entry failed: {ex.Message}");
            return false;
        }
    }

    private static T? Call<T>(string name) where T : struct
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc();
        }
        catch
        {
            return null;
        }
    }
}

internal static class LifestreamIpc
{
    public static bool Available
    {
        get
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsBusy()
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public static void ExecuteCommand(string command)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(command);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Lifestream command '{command}' failed: {ex.Message}");
        }
    }
}
