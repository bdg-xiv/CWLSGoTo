using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

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
        StartingRetainers,
        WaitingForRetainers,
        Done,
    }

    // Lifestream's trip home is a teleport plus a walk; retainers are a few menus each.
    private const int TravelTimeoutMs = 180_000;
    private const int RetainerTimeoutMs = 600_000;

    // AutoRetainer needs a moment to pick the work up before its busy flag means anything.
    private const int StartGraceMs = 5_000;

    private readonly Configuration config;

    private Stage stage = Stage.Idle;
    private long stageSince;
    private bool resumeGathering;

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
        GatherBuddyIpc.SetAutoGatherEnabled(false);
        Report("Retainers are ready - pausing the gather run and heading home.");
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
                    Enter(Stage.StartingRetainers);
                break;

            case Stage.StartingRetainers:
                AutoRetainerIpc.StartSingleCharacter();
                Status = "AutoRetainer is working...";
                Enter(Stage.WaitingForRetainers);
                break;

            case Stage.WaitingForRetainers:
                if (elapsed > RetainerTimeoutMs)
                {
                    Finish("AutoRetainer took too long; resuming gathering.", resume: true);
                    break;
                }

                if (elapsed > StartGraceMs && AutoRetainerIpc.IsBusy() == false && AutoRetainerIpc.MultiModeRunning() == false)
                    Finish("Retainers done - back to gathering.", resume: resumeGathering);
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

    public static bool? MultiModeRunning() => Call<bool>("AutoRetainer.PluginState.GetMultiModeStatus");

    /// <summary>Single-character multi mode: AutoRetainer walks to the bell and processes
    /// this character's retainers without relogging anywhere else.</summary>
    public static void StartSingleCharacter()
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.PluginState.EnableMultiModeSingle")
                .InvokeAction();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"AutoRetainer single multi mode failed: {ex.Message}");
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
