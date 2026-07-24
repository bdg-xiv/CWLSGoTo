using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;

namespace StackSplitter;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const int StackTarget = 50;
    private const int SplitIntervalMs = 650;
    private const int NoProgressTimeoutMs = 4000;
    private const int RunTimeoutMs = 60000;

    // The active split run: keep splitting every bag stack of the item (matching HQ
    // state) until none holds more than the target.
    private bool running;
    private uint runItemId;
    private bool runHq;
    private string runItemName = "";
    private long runDeadline;
    private long lastProgressAt;
    private long lastTotalOversize = -1;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        ECommonsMain.Dispose();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory { TargetItem: { } item })
            return;

        // Only offer it for player bag stacks that are actually splittable.
        if (item.ContainerType is not (Dalamud.Game.Inventory.GameInventoryType.Inventory1
            or Dalamud.Game.Inventory.GameInventoryType.Inventory2
            or Dalamud.Game.Inventory.GameInventoryType.Inventory3
            or Dalamud.Game.Inventory.GameInventoryType.Inventory4))
            return;

        var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId);
        if (row == null || row.Value.StackSize <= 1 || item.Quantity <= StackTarget)
            return;

        var itemId = item.ItemId;
        var hq = item.IsHq;
        args.AddMenuItem(new MenuItem
        {
            Name = $"Split into stacks of {StackTarget}",
            Prefix = SeIconChar.BoxedLetterS,
            PrefixColor = 37,
            OnClicked = _ => StartSplit(itemId, hq),
        });
    }

    private void StartSplit(uint itemId, bool hq)
    {
        if (running)
        {
            Svc.Chat.Print("[StackSplitter] A split is already running.");
            return;
        }

        running = true;
        runItemId = itemId;
        runHq = hq;
        runItemName = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item {itemId}";
        runDeadline = Environment.TickCount64 + RunTimeoutMs;
        lastProgressAt = Environment.TickCount64;
        lastTotalOversize = -1;
        Svc.Chat.Print($"[StackSplitter] Splitting {runItemName} into stacks of {StackTarget}...");
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (!running)
            return;

        var now = Environment.TickCount64;
        if (now > runDeadline)
        {
            Finish("took too long - stopped");
            return;
        }

        // Find the largest oversized bag stack of the item and track total overflow
        // for progress detection.
        var manager = InventoryManager.Instance();
        InventoryType bestContainer = default;
        var bestSlot = -1;
        long bestQty = 0;
        long totalOversize = 0;
        foreach (var bag in (InventoryType[])[InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4])
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != runItemId
                    || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != runHq
                    || slot->Quantity <= StackTarget)
                    continue;

                totalOversize += slot->Quantity;
                if (slot->Quantity > bestQty)
                {
                    bestQty = slot->Quantity;
                    bestContainer = bag;
                    bestSlot = i;
                }
            }
        }

        if (bestSlot < 0)
        {
            Finish("done");
            return;
        }

        if (totalOversize != lastTotalOversize)
        {
            lastTotalOversize = totalOversize;
            lastProgressAt = now;
        }
        else if (now - lastProgressAt > NoProgressTimeoutMs)
        {
            Finish("no progress - are your bags full?");
            return;
        }

        if (EzThrottler.Throttle("StackSplitter.Split", SplitIntervalMs))
            manager->SplitItem(bestContainer, (ushort)bestSlot, StackTarget);
    }

    private void Finish(string reason)
    {
        running = false;
        if (reason == "done")
            Svc.Chat.Print($"[StackSplitter] {runItemName} is now in stacks of {StackTarget} or less.");
        else
            Svc.Chat.Print($"[StackSplitter] {runItemName}: {reason}.");
    }
}
