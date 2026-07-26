using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.GenericHelpers;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Laziness;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/laziness";

    // Idyllshire: Hismena exchanges poetics for the unidentifiable items, Bertana
    // trades those for topsoil. ENpcResident row ids double as object data ids.
    private const uint HismenaDataId = 1012228;
    private const uint BertanaDataId = 1015578;
    private const uint UnidentifiableShellId = 13584;
    private const uint Grade3ShroudTopsoilId = 7763;
    private const uint PoeticsItemId = 28;

    // The game takes at most 99 units per exchange, however much you can afford.
    private const int MaxPerPurchase = 99;
    private const int StepTimeoutMs = 15000;

    private readonly WindowSystem windowSystem = new("Laziness");
    private readonly MainWindow mainWindow;
    private readonly TaskManager taskManager;
    private readonly Configuration configuration;

    private int shellsAtStart;
    private int topsoilAtStart;
    private int poeticsAtStart;
    private int shellsBought;

    internal bool Running => taskManager.IsBusy;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        taskManager = new TaskManager(new TaskManagerConfiguration
        {
            TimeLimitMS = StepTimeoutMs,
            AbortOnTimeout = true,
            ShowError = false,
        });

        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        mainWindow = new MainWindow(this, configuration) { IsOpen = configuration.WindowOpen };
        windowSystem.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Opens the Laziness window."
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        windowSystem.RemoveAllWindows();
        ECommonsMain.Dispose();
    }

    internal void Abort()
    {
        taskManager.Abort();
        Print("Stopped.");
    }

    private static void SetStatus(string status) => Svc.Log.Information($"[Laziness] {status}");

    /// <summary>Results go to the echo channel so they sit in the log with the rest
    /// of the plugin chatter rather than in Dalamud's debug channel.</summary>
    private static void Print(string message)
        => Svc.Chat.Print(new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = new SeStringBuilder().AddUiForeground("[Laziness] ", 45).AddText(message).Build(),
        });

    internal static unsafe int CountOf(uint itemId)
        => InventoryManager.Instance()->GetInventoryItemCount(itemId);

    /// <summary>Poetics and other currencies live outside the bags, so they need the
    /// currency container explicitly.</summary>
    internal static unsafe int CurrencyCount(uint itemId)
    {
        var manager = InventoryManager.Instance();
        var container = manager->GetInventoryContainer(InventoryType.Currency);
        if (container == null)
            return manager->GetInventoryItemCount(itemId);

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId == itemId)
                return (int)slot->Quantity;
        }

        return 0;
    }

    private static unsafe int FreeBagSlots()
    {
        var manager = InventoryManager.Instance();
        var free = 0;
        foreach (var bag in (InventoryType[])[InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4])
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>Room for more of an item: a partial stack counts, otherwise a free slot is needed.</summary>
    private static unsafe bool HasRoomFor(uint itemId)
    {
        if (FreeBagSlots() > 0)
            return true;

        var stackSize = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 1;
        var manager = InventoryManager.Instance();
        foreach (var bag in (InventoryType[])[InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4])
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId && slot->Quantity < stackSize)
                    return true;
            }
        }

        return false;
    }

    internal void StartBuySoil()
    {
        if (taskManager.IsBusy)
        {
            Print("Already running - stop it first.");
            return;
        }

        if (!Svc.ClientState.IsLoggedIn)
            return;

        poeticsAtStart = CurrencyCount(PoeticsItemId);
        shellsAtStart = CountOf(UnidentifiableShellId);
        topsoilAtStart = CountOf(Grade3ShroudTopsoilId);
        shellsBought = 0;

        SetStatus($"Starting with {poeticsAtStart:N0} poetics.");

        // Hismena: poetics -> Unidentifiable Shell.
        Enqueue(() => InteractWith(HismenaDataId, "Hismena"), "Interact with Hismena");
        Enqueue(() => OpenShopMenu(["special arms", "poetics", "tomestone"]), "Open Hismena's shop");
        Enqueue(BuyShells, "Buy Unidentifiable Shells", 120000);
        Enqueue(RecordShellsBought, "Count shells bought");
        Enqueue(CloseShopWindows, "Close Hismena's shop");

        // Bertana: Unidentifiable Shell -> Grade 3 Shroud Topsoil.
        Enqueue(() => InteractWith(BertanaDataId, "Bertana"), "Interact with Bertana");
        Enqueue(() => OpenShopMenu(["uncanny", "knickknack", "exchange"]), "Open Bertana's shop");
        Enqueue(BuyTopsoil, "Buy Grade 3 Shroud Topsoil", 120000);
        Enqueue(CloseShopWindows, "Close Bertana's shop");
        Enqueue(FinishRun, "Report");
    }

    private void Enqueue(Func<bool?> task, string name, int timeoutMs = StepTimeoutMs)
        => taskManager.Enqueue(task, name, new TaskManagerConfiguration { TimeLimitMS = timeoutMs, AbortOnTimeout = true, ShowError = false });

    /// <summary>Shells in hand once Hismena's part is done, so the report counts what
    /// was actually bought rather than what was asked for.</summary>
    private bool? RecordShellsBought()
    {
        shellsBought = Math.Max(CountOf(UnidentifiableShellId) - shellsAtStart, 0);
        return true;
    }

    private bool? FinishRun()
    {
        var poeticsSpent = poeticsAtStart - CurrencyCount(PoeticsItemId);
        var topsoilBought = Math.Max(CountOf(Grade3ShroudTopsoilId) - topsoilAtStart, 0);

        SetStatus($"Done - {topsoilBought} topsoil for {poeticsSpent:N0} poetics.");
        Print($"Bought {shellsBought} Unidentifiable Shell and {topsoilBought} Grade 3 Shroud Topsoil "
            + $"for {poeticsSpent:N0} poetics.");
        return true;
    }

    // ---- NPC interaction --------------------------------------------------------

    private unsafe bool? InteractWith(uint dataId, string npcName)
    {
        // A shop window already being open means we're there.
        if (ShopIsOpen())
            return true;

        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var icons) && icons.IsAddonReady)
            return true;
        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var strings) && strings.IsAddonReady)
            return true;

        var npc = Svc.Objects.FirstOrDefault(o => o.BaseId == dataId);
        if (npc == null)
        {
            SetStatus($"{npcName} isn't nearby.");
            Print($"{npcName} isn't nearby - stand next to her in Idyllshire and try again.");
            return null; // aborts the queue
        }

        if (!EzThrottler.Throttle($"Laziness.Interact.{dataId}", 2000))
            return false;

        SetStatus($"Talking to {npcName}...");
        var target = TargetSystem.Instance();
        target->Target = (GameObjectStruct*)npc.Address;
        target->InteractWithObject((GameObjectStruct*)npc.Address, false);
        return false;
    }

    private static bool ClickTalkIfOpen()
    {
        if (TryGetAddonMaster<AddonMaster.Talk>("Talk", out var talk) && talk.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Talk", 300))
                talk.Click();
            return true;
        }

        return false;
    }

    private static unsafe bool ShopIsOpen()
        => (TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrency", out var currency) && IsAddonReady(currency))
        || (TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var item) && IsAddonReady(item));

    /// <summary>Walks the dialogue menus until a shop window is up, choosing entries by
    /// keyword so it works whatever the menu depth is.</summary>
    private unsafe bool? OpenShopMenu(string[] keywords)
    {
        if (ShopIsOpen())
            return true;

        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectIconString>("SelectIconString", out var icons) && icons.IsAddonReady)
            return SelectMenuEntry(icons.Entries.Select(e => e.Text).ToArray(), keywords, i => icons.Entries[i].Select());

        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var strings) && strings.IsAddonReady)
            return SelectMenuEntry(strings.Entries.Select(e => e.Text).ToArray(), keywords, i => strings.Entries[i].Select());

        return false;
    }

    private bool? SelectMenuEntry(string[] entries, string[] keywords, Action<int> select)
    {
        if (entries.Length == 0)
            return false;

        if (!EzThrottler.Throttle("Laziness.Menu", 1000))
            return false;

        foreach (var keyword in keywords)
        {
            var index = Array.FindIndex(entries, e => e.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                SetStatus($"Menu: {entries[index]}");
                select(index);
                return false;
            }
        }

        // Nothing matched - stop rather than guessing and buying the wrong thing.
        SetStatus("Couldn't find the shop entry in the menu.");
        Print("Couldn't tell which menu entry opens the shop. Entries were: " + string.Join(" | ", entries));
        return null;
    }

    private static unsafe bool? CloseShopWindows()
    {
        foreach (var name in (string[])["ShopExchangeCurrency", "ShopExchangeItem"])
        {
            if (TryGetAddonByName<AtkUnitBase>(name, out var addon) && IsAddonReady(addon))
            {
                if (EzThrottler.Throttle("Laziness.CloseShop", 500))
                    addon->Close(true);
                return false;
            }
        }

        return true;
    }

    // ---- Purchasing -------------------------------------------------------------

    /// <summary>Spends poetics on Unidentifiable Shells until they run out.</summary>
    private unsafe bool? BuyShells()
    {
        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Yes", 500))
                yesno.Yes();
            return false;
        }

        if (!TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop) || !shop.IsAddonReady)
            return false;

        var entry = shop.BasicShopItems.FirstOrDefault(x => x.ItemId == UnidentifiableShellId);
        if (entry == null)
        {
            SetStatus("Unidentifiable Shell isn't in this shop.");
            Print("This shop doesn't sell Unidentifiable Shell - wrong menu entry?");
            return null;
        }

        var cost = (int)Math.Max(entry.CostAmount, 1);
        var affordable = (int)shop.CurrencyAmount / cost;
        if (affordable <= 0)
        {
            SetStatus($"Out of poetics ({shop.CurrencyAmount:N0} left, {cost} each).");
            return true;
        }

        if (!HasRoomFor(UnidentifiableShellId))
        {
            SetStatus("Inventory full.");
            Print("No inventory room for more shells - stopping here.");
            return true;
        }

        var amount = Math.Min(affordable, MaxPerPurchase);
        if (!EzThrottler.Throttle("Laziness.BuyShell", 1000))
            return false;

        SetStatus($"Buying {amount} shell(s) at {cost} poetics each...");
        entry.Select(amount);
        return false;
    }

    /// <summary>Trades every Unidentifiable Shell for Grade 3 Shroud Topsoil.</summary>
    private unsafe bool? BuyTopsoil()
    {
        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Yes", 500))
                yesno.Yes();
            return false;
        }

        if (TryGetAddonMaster<AddonMaster.ShopExchangeItemDialog>("ShopExchangeItemDialog", out var dialog) && dialog.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Exchange", 500))
                dialog.Exchange();
            return false;
        }

        if (!TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var addon) || !IsAddonReady(addon))
            return false;

        var shop = new ItemExchangeShop(addon);
        var row = shop.Rows().FirstOrDefault(r => r.ItemId == Grade3ShroudTopsoilId);
        if (row == null)
        {
            SetStatus("Grade 3 Shroud Topsoil isn't in this shop.");
            Print("This shop doesn't trade Grade 3 Shroud Topsoil - wrong menu entry?");
            return null;
        }

        var shellCost = row.Costs.FirstOrDefault(c => c.ItemId == UnidentifiableShellId);
        var cost = (int)Math.Max(shellCost?.Amount ?? 1, 1);
        var shells = CountOf(UnidentifiableShellId);
        var affordable = shells / cost;
        if (affordable <= 0)
        {
            SetStatus($"Out of shells ({shells} left, {cost} each).");
            return true;
        }

        if (!HasRoomFor(Grade3ShroudTopsoilId))
        {
            SetStatus("Inventory full.");
            Print("No inventory room for more topsoil - stopping here.");
            return true;
        }

        var amount = Math.Min(affordable, MaxPerPurchase);
        if (!EzThrottler.Throttle("Laziness.BuyTopsoil", 1000))
            return false;

        SetStatus($"Trading {amount} shell(s) for topsoil...");
        shop.Select(row, amount);
        return false;
    }

}
