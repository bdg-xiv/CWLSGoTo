using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using static ECommons.GenericHelpers;
using Callback = ECommons.Automation.Callback;

namespace DesynthAllCommand;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/desynthall";

    // Same event codes ECommons/PandorasBox use to drive the SalvageItemSelector/SalvageDialog addons.
    // Event 12's value is the row index in the list, so it can select any item, not just the first.
    private const int SelectItemEventCode = 12;
    private const int ConfirmDialogEventCode = 0;

    private readonly TaskManager taskManager;
    private bool yesAlreadyLocked;
    private bool configOpen;

    // Highest item level among all desynthesizable items = the hard cap desynthesis
    // skill can reach. At or above it nothing grants skill anymore. Computed lazily.
    private uint maxDesynthLevel;

    internal Configuration Config { get; }

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // TaskManager's constructor hooks Svc.Framework.Update, so it must be created
        // after ECommonsMain.Init - a field initializer would run too early and throw.
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = true });

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Desynthesizes everything in the desynthesis window. \"/desynthall config\" opens the settings."
        });

        PluginInterface.UiBuilder.Draw += DrawConfigWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;
    }

    private void OpenConfigWindow() => configOpen = true;

    private void DrawConfigWindow()
    {
        if (!configOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("DesynthAll Settings###DesynthAllSettings", ref configOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            var onlySkillGain = Config.OnlySkillGain;
            if (ImGui.Checkbox("Only items that grant desynthesis skill", ref onlySkillGain))
            {
                Config.OnlySkillGain = onlySkillGain;
                Config.Save();
            }
            ImGui.TextDisabled("Desynthesizes the items SimpleTweaks colors yellow/red and\nignores the green ones (your skill is 50+ above their item level).");

            ImGui.Spacing();

            var skipGearset = Config.SkipGearsetItems;
            if (ImGui.Checkbox("Never desynthesize gear set items", ref skipGearset))
            {
                Config.SkipGearsetItems = skipGearset;
                Config.Save();
            }
        }
        ImGui.End();
    }

    private unsafe void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase)
            || args.Trim().Equals("cfg", StringComparison.OrdinalIgnoreCase))
        {
            configOpen = !configOpen;
            return;
        }

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

        var next = FindNextEligibleItem(out var skippedNoSkill, out var skippedGearset);
        if (next < 0)
        {
            var summary = "[DesynthAll] Done.";
            if (skippedNoSkill > 0)
                summary += $" Ignored {skippedNoSkill} item(s) that grant no desynthesis skill.";
            if (skippedGearset > 0)
                summary += $" Ignored {skippedGearset} gear set item(s).";
            Svc.Log.Information(summary);
            Svc.Chat.Print(summary);
            UnlockYesAlready();
            addon->AtkUnitBase.Close(true);
            return true;
        }

        Svc.Log.Information($"Desynthesizing next eligible item (index {next}, {addon->ItemCount} in list)");
        // DesynthNext can end up waiting a while for the lingering occupied state from
        // the previous item to clear, so give it a long leash and never kill the whole
        // run over it - the outer loop keeps retrying regardless.
        taskManager.Enqueue(DesynthNext, "DesynthNext", new TaskManagerConfiguration { TimeLimitMS = 60000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(ConfirmDesynth, "ConfirmDesynth", new TaskManagerConfiguration { TimeLimitMS = 2000, AbortOnTimeout = false });
        taskManager.Enqueue(CloseResults, "CloseResults", new TaskManagerConfiguration { TimeLimitMS = 9000, AbortOnTimeout = false });
        taskManager.EnqueueDelay(500);
        taskManager.Enqueue(DesynthLoop, "DesynthLoop");
        return true;
    }

    private unsafe bool? DesynthNext()
    {
        // The game rejects the request with "Unable to execute command while occupied"
        // if it's fired while any occupied-type condition is still set - Occupied39 in
        // particular lingers briefly after the previous item's result window closes.
        // IsOccupied() covers the full set of those flags; keep retrying until clear.
        if (IsOccupied())
        {
            if (EzThrottler.Throttle("DesynthAllOccupiedLog", 1000))
                Svc.Log.Information("Waiting for the occupied state to clear before desynthesizing the next item");
            return false;
        }

        if (!TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var addon))
            return null;

        // Re-scan at fire time: the agent refreshes its list after each desynth, so an
        // index computed earlier (while the occupied state was still clearing) could be stale.
        var index = FindNextEligibleItem(out _, out _);
        if (index < 0)
            return true; // Nothing eligible anymore; the outer loop prints the summary.

        Callback.Fire(addon, false, SelectItemEventCode, index);
        return true;
    }

    /// <summary>
    /// Index of the first item in the desynthesis list that passes the configured filters,
    /// or -1 when none does. Also counts how many list items each filter rejected.
    /// </summary>
    private unsafe int FindNextEligibleItem(out int skippedNoSkill, out int skippedGearset)
    {
        skippedNoSkill = 0;
        skippedGearset = 0;

        var agent = AgentSalvage.Instance();
        if (agent == null)
            return -1;

        for (var i = 0; i < agent->ItemCount; i++)
        {
            var entry = agent->ItemList + i;

            if (Config.SkipGearsetItems && IsInGearset(entry))
            {
                skippedGearset++;
                continue;
            }

            if (Config.OnlySkillGain && !GrantsDesynthSkill(entry->ItemId))
            {
                skippedNoSkill++;
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Same check SimpleTweaks' Extended Desynthesis Window uses for its yellow/red vs green
    /// coloring: skill still rises while the class's desynthesis level is below the item's
    /// item level + 50 and below the game-wide cap (the highest desynthesizable item level).
    /// </summary>
    private unsafe bool GrantsDesynthSkill(uint itemId)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return false;

        var desynthLevel = PlayerState.Instance()->GetDesynthesisLevel(item.Value.ClassJobRepair.RowId);
        return desynthLevel < MaxDesynthLevel && desynthLevel < item.Value.LevelItem.RowId + 50;
    }

    private uint MaxDesynthLevel
    {
        get
        {
            if (maxDesynthLevel == 0)
            {
                foreach (var item in Svc.Data.GetExcelSheet<Item>())
                {
                    if (item.Desynth > 0 && item.LevelItem.RowId > maxDesynthLevel)
                        maxDesynthLevel = item.LevelItem.RowId;
                }
            }

            return maxDesynthLevel;
        }
    }

    private static unsafe bool IsInGearset(AgentSalvage.SalvageListItem* entry)
    {
        // Gear sets store HQ items as item id + 1,000,000. The HQ flag lives on the
        // inventory slot, so resolve it; if the slot can't be resolved (list briefly out
        // of sync with the inventory), check both variants to stay on the safe side.
        var itemId = entry->ItemId;
        uint? gearsetItemId = null;

        var container = InventoryManager.Instance()->GetInventoryContainer(entry->InventoryType);
        var slot = container != null ? container->GetInventorySlot((int)entry->InventorySlot) : null;
        if (slot != null && slot->ItemId == itemId)
            gearsetItemId = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? itemId + 1_000_000 : itemId;

        var module = RaptureGearsetModule.Instance();
        for (var i = 0; i < 101; i++)
        {
            var gearset = module->GetGearset(i);
            if (gearset == null)
                continue;
            if (gearset->Id != i)
                break;
            if (!gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (ref var gearsetItem in gearset->Items)
            {
                if (gearsetItemId != null
                        ? gearsetItem.ItemId == gearsetItemId.Value
                        : gearsetItem.ItemId == itemId || gearsetItem.ItemId == itemId + 1_000_000)
                    return true;
            }
        }

        return false;
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
        PluginInterface.UiBuilder.Draw -= DrawConfigWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        taskManager.Abort();
        UnlockYesAlready();
        Svc.Commands.RemoveHandler(CommandName);
        taskManager.Dispose();
        ECommonsMain.Dispose();
    }
}
