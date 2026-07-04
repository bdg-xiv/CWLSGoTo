using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using static ECommons.GenericHelpers;

namespace DesynthAllCommand;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/desynthall";

    // Same event codes ECommons/PandorasBox use to drive the SalvageItemSelector/SalvageDialog addons.
    private const int SelectFirstItemEventCode = 12;
    private const int ConfirmDialogEventCode = 0;

    private readonly TaskManager taskManager;
    private bool yesAlreadyLocked;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        // TaskManager's constructor hooks Svc.Framework.Update, so it must be created
        // after ECommonsMain.Init - a field initializer would run too early and throw.
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = true });

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the desynthesis window and desynthesizes everything in it automatically."
        });
    }

    private unsafe void OnCommand(string command, string args)
    {
        taskManager.Abort();

        // AgentInterface.Show() toggles: calling it while the window is open closes it,
        // so only open the window when it isn't there yet.
        if (!TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _))
        {
            Svc.Log.Information("Opening the desynthesis window");
            OpenDesynthesisWindow();
        }

        LockYesAlready();

        taskManager.Enqueue(WaitForWindow, "WaitForWindow", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = true });
        // The item list populates asynchronously after the window opens, so give it a
        // grace period before concluding there is nothing to desynthesize. If the list
        // is genuinely empty this times out and falls through to DesynthLoop's 0 branch.
        taskManager.Enqueue(WaitForItems, "WaitForItems", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(DesynthLoop, "DesynthLoop");
    }

    private unsafe void OpenDesynthesisWindow()
    {
        var agent = AgentModule.Instance()->GetAgentSalvage();
        if (agent == null)
        {
            Svc.Log.Warning("Could not find the Salvage agent to open the desynthesis window.");
            return;
        }

        ((AgentInterface*)agent)->Show();
    }

    private static unsafe bool? WaitForWindow()
        => TryGetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", out var addon) && IsAddonReady(&addon->AtkUnitBase);

    private static unsafe bool? WaitForItems()
        => TryGetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", out var addon) && addon->ItemCount > 0;

    private unsafe bool? DesynthLoop()
    {
        if (!TryGetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", out var addon))
        {
            Svc.Log.Warning("Desynthesis window closed, stopping.");
            UnlockYesAlready();
            return null; // Aborts the rest of the queue.
        }

        if (addon->ItemCount == 0)
        {
            Svc.Log.Information("No items left to desynthesize, done.");
            Svc.Chat.Print("[DesynthAll] Done.");
            UnlockYesAlready();
            return true;
        }

        Svc.Log.Information($"Desynthesizing next item ({addon->ItemCount} in list)");
        taskManager.Enqueue(DesynthFirst, "DesynthFirst");
        taskManager.Enqueue(ConfirmDesynth, "ConfirmDesynth", new TaskManagerConfiguration { TimeLimitMS = 2000, AbortOnTimeout = false });
        taskManager.Enqueue(CloseResults, "CloseResults", new TaskManagerConfiguration { TimeLimitMS = 9000, AbortOnTimeout = false });
        taskManager.EnqueueDelay(500);
        taskManager.Enqueue(DesynthLoop, "DesynthLoop");
        return true;
    }

    private static unsafe bool? DesynthFirst()
    {
        if (Svc.Condition[ConditionFlag.Occupied])
            return false;

        if (!TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var addon))
            return null;

        Callback.Fire(addon, false, SelectFirstItemEventCode, 0);
        return true;
    }

    private static unsafe bool? ConfirmDesynth()
    {
        if (Svc.Condition[ConditionFlag.Occupied])
            return false;

        if (!TryGetAddonByName<AtkUnitBase>("SalvageDialog", out var addon) || !addon->IsVisible)
            return false;

        Callback.Fire(addon, false, ConfirmDialogEventCode, false);
        return Svc.Condition[ConditionFlag.Occupied39];
    }

    private static unsafe bool? CloseResults()
    {
        if (Svc.Condition[ConditionFlag.Occupied])
            return false;

        if (!TryGetAddonByName<AtkUnitBase>("SalvageResult", out var addon) || !addon->IsVisible)
            return false;

        addon->Close(true);
        return true;
    }

    // Mirrors PandorasBox's own Yes Already lock/unlock so it doesn't race us on the confirm dialog.
    private void LockYesAlready()
    {
        try
        {
            if (Svc.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data))
            {
                data.Add(Svc.PluginInterface.InternalName);
                yesAlreadyLocked = true;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose($"Could not lock Yes Already: {ex.Message}");
        }
    }

    private void UnlockYesAlready()
    {
        if (!yesAlreadyLocked)
            return;

        try
        {
            if (Svc.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data))
                data.Remove(Svc.PluginInterface.InternalName);
        }
        catch (Exception ex)
        {
            Svc.Log.Verbose($"Could not unlock Yes Already: {ex.Message}");
        }

        yesAlreadyLocked = false;
    }

    public void Dispose()
    {
        taskManager.Abort();
        UnlockYesAlready();
        Svc.Commands.RemoveHandler(CommandName);
        taskManager.Dispose();
        ECommonsMain.Dispose();
    }
}
