using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using static ECommons.GenericHelpers;

namespace FaloopScreener;

/// <summary>Automates the levequest spawn method (Wulgaru / Lampalagua): initiate the
/// accepted leve, watch for the "powerful mark" message, abandon and re-initiate until
/// it appears or the attempt limit runs out. Mechanics mirror Battlevest: the journal
/// agent opens the leve, ECommons' JournalDetail master clicks Initiate/Abandon, the
/// GuildLeveDifficulty confirm is button 7, and ConditionFlag 34 marks an active leve.
/// Each initiation costs one leve allowance; abandoning keeps the leve reusable.</summary>
internal sealed unsafe class LeveSpawner : IDisposable
{
    // Set while a levequest is in progress (what Battlevest checks around initiation).
    private const ConditionFlag LeveInProgress = (ConditionFlag)34;

    private readonly TaskManager taskManager;

    private ushort leveId;
    private int maxAttempts;
    private int attemptsDone;
    private volatile bool markSeen;

    public bool IsRunning => taskManager.IsBusy;
    public int AttemptsDone => attemptsDone;
    public string Status { get; private set; } = "";

    public LeveSpawner()
    {
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 20000, AbortOnTimeout = false, TimeoutSilently = true });
        Svc.Chat.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
        taskManager.Abort();
        taskManager.Dispose();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!IsRunning || markSeen)
            return;

        // "You sense the presence of a powerful mark..." - the zone-wide S-rank spawn line.
        var text = message.Message.TextValue;
        if (text.Contains("powerful mark", StringComparison.OrdinalIgnoreCase))
        {
            markSeen = true;
            Svc.Log.Information($"Powerful mark message seen: {text}");
        }
    }

    public void Start(ushort targetLeveId, int attempts)
    {
        if (IsRunning)
            return;

        leveId = targetLeveId;
        maxAttempts = Math.Clamp(attempts, 1, 99);
        attemptsDone = 0;
        markSeen = false;
        Status = "Starting...";
        EnqueueCycle();
    }

    public void Stop()
    {
        taskManager.Abort();
        Status = $"Stopped after {attemptsDone} attempt(s).";
    }

    private void EnqueueCycle()
    {
        taskManager.Enqueue(AbandonIfActive, "AbandonIfActive");
        taskManager.Enqueue(Initiate, "Initiate");
        taskManager.EnqueueDelay(1500); // give the spawn message time to arrive
        taskManager.Enqueue(LoopOrFinish, "LoopOrFinish");
    }

    /// <summary>Abandons the active leve (also used after a successful spawn so the
    /// leve stays in the journal for future windows).</summary>
    private bool? AbandonIfActive()
    {
        if (!Svc.Condition[LeveInProgress])
            return true;

        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("FSLeve.Yes", 500))
                yesno.Yes();
            return false;
        }

        if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var detail) && detail.IsAddonReady)
        {
            if (EzThrottler.Throttle("FSLeve.Abandon", 1000))
                detail.AbandonDecline();
            return false;
        }

        if (EzThrottler.Throttle("FSLeve.OpenJournal", 2000))
            AgentQuestJournal.Instance()->OpenForQuest(leveId, 2u, 0, true);
        return false;
    }

    private bool? Initiate()
    {
        if (markSeen)
            return true;

        Status = $"Attempt {attemptsDone + 1}/{maxAttempts}: initiating...";

        if (Svc.Condition[LeveInProgress])
            return true; // Initiated - the spawn roll has happened.

        if (TryGetAddonByName<AtkUnitBase>("GuildLeveDifficulty", out var difficulty) && IsAddonReady(difficulty))
        {
            var confirm = difficulty->GetComponentButtonById(7);
            if (confirm != null && confirm->IsEnabled && EzThrottler.Throttle("FSLeve.Difficulty", 500))
                (*confirm).ClickAddonButton(difficulty);
            return false;
        }

        if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var detail) && detail.IsAddonReady && detail.CanInitiate)
        {
            if (EzThrottler.Throttle("FSLeve.Initiate", 1000))
            {
                attemptsDone++;
                detail.Initiate();
            }

            return false;
        }

        if (EzThrottler.Throttle("FSLeve.OpenJournal", 2000))
            AgentQuestJournal.Instance()->OpenForQuest(leveId, 2u, 0, true);
        return false;
    }

    private bool? LoopOrFinish()
    {
        if (markSeen)
        {
            // Keep the leve for next time: abandon the initiated one, then report.
            taskManager.Enqueue(AbandonIfActive, "AbandonAfterSuccess");
            taskManager.Enqueue(() =>
            {
                Status = $"MARK SPAWNED after {attemptsDone} attempt(s)!";
                Svc.Chat.Print($"[FaloopScreener] A powerful mark appeared after {attemptsDone} attempt(s) - go get it!");
                return true;
            }, "ReportSuccess");
            return true;
        }

        if (!Svc.ClientState.IsLoggedIn)
        {
            Status = "Stopped: not logged in.";
            return true;
        }

        if (attemptsDone >= maxAttempts)
        {
            Status = $"No spawn after {attemptsDone} attempt(s).";
            Svc.Chat.Print($"[FaloopScreener] No mark after {attemptsDone} leve attempt(s). "
                + "Consider waiting and trying again later in the window.");
            taskManager.Enqueue(AbandonIfActive, "AbandonAtEnd");
            return true;
        }

        if (RemainingAllowances() == 0)
        {
            Status = "Stopped: no leve allowances left.";
            Svc.Chat.Print("[FaloopScreener] Out of leve allowances, stopping.");
            taskManager.Enqueue(AbandonIfActive, "AbandonAtEnd");
            return true;
        }

        EnqueueCycle();
        return true;
    }

    public static byte RemainingAllowances()
        => FFXIVClientStructs.FFXIV.Client.Game.QuestManager.Instance()->NumLeveAllowances;
}
