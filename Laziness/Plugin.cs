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
using System.Threading.Tasks;
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

    // One hunt billmaster per city - match all three so it works wherever you are.
    private static readonly uint[] HuntBillmasterDataIds = [1001379, 1009152, 1009552];
    private const uint ArdolainDataId = 1012225;
    private const uint AlliedSealId = 27;
    private const uint CenturioSealId = 10307;
    private const uint VentureId = 21072;
    private const uint AetheryteTicketId = 7569;

    // Zircon in Solution Nine runs the "Allagan Tomestones of Mathematics (Other)"
    // counter - the only tradeable things those tomestones buy, all at the same cost.
    private const uint ZirconDataId = 1049079;
    private const uint MathsTomeId = 48;
    private const int MathsUnitCost = 20;
    private static readonly Dictionary<uint, string> MathsWares = new()
    {
        [49223] = "Insulating Varnish",
        [49224] = "Double Duracoat",
        [49225] = "Everkeep Resin",
        [49226] = "Mastodon Pelt",
        [49227] = "Turali Pigment",
        [49228] = "Yollal Extract",
    };

    // The game takes at most 99 units per exchange, however much you can afford.
    private const int MaxPerPurchase = 99;
    private const int StepTimeoutMs = 15000;

    private readonly WindowSystem windowSystem = new("Laziness");
    private readonly MainWindow mainWindow;
    private readonly TaskManager taskManager;
    private readonly Configuration configuration;

    // Run bookkeeping: everything reported comes from inventory deltas.
    private uint buyTargetItemId;
    private uint spentCurrencyId;
    private int currencyAtStart;
    private int targetAtStart;
    private int shellsAtStart;
    private int shellsBought;

    private volatile bool consultingMarket;

    internal bool Running => taskManager.IsBusy || consultingMarket;

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

    private static string NameOf(uint itemId)
        => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";

    // ---- Inventory --------------------------------------------------------------

    internal static unsafe int CountOf(uint itemId)
        => InventoryManager.Instance()->GetInventoryItemCount(itemId);

    /// <summary>Seals and tomestones live outside the bags, so they need the currency
    /// container explicitly.</summary>
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

    // ---- Chores -----------------------------------------------------------------

    internal void StartBuySoil()
    {
        if (!CanStart())
            return;

        shellsAtStart = CountOf(UnidentifiableShellId);
        shellsBought = 0;
        targetAtStart = CountOf(Grade3ShroudTopsoilId);
        spentCurrencyId = PoeticsItemId;
        currencyAtStart = CurrencyCount(PoeticsItemId);
        buyTargetItemId = UnidentifiableShellId;

        SetStatus($"Buy soil: starting with {currencyAtStart:N0} poetics.");

        // Hismena: poetics -> Unidentifiable Shell.
        Enqueue(() => InteractWith(HismenaDataIds, "Hismena"), "Interact with Hismena");
        Enqueue(() => OpenShopMenu(["special arms", "poetics", "tomestone"]), "Open Hismena's shop");
        Enqueue(BuyFromCurrencyShop, "Buy Unidentifiable Shells", 120000);
        Enqueue(RecordShellsBought, "Count shells bought");
        Enqueue(CloseShopWindows, "Close Hismena's shop");

        // Bertana: Unidentifiable Shell -> Grade 3 Shroud Topsoil.
        Enqueue(() => InteractWith([BertanaDataId], "Bertana"), "Interact with Bertana");
        Enqueue(() => OpenShopMenu(["uncanny", "knickknack", "exchange"]), "Open Bertana's shop");
        Enqueue(BuyTopsoil, "Buy Grade 3 Shroud Topsoil", 120000);
        Enqueue(CloseShopWindows, "Close Bertana's shop");
        Enqueue(ReportSoilRun, "Report");
    }

    private static readonly uint[] HismenaDataIds = [HismenaDataId];

    /// <summary>Spends hunt seals on whichever of ventures / aetheryte tickets you
    /// currently hold fewer of.</summary>
    internal void StartSealExchange(bool centurio)
    {
        if (!CanStart())
            return;

        var currencyId = centurio ? CenturioSealId : AlliedSealId;
        var seals = CurrencyCount(currencyId);
        if (seals <= 0)
        {
            Print($"No {NameOf(currencyId)}s to spend.");
            return;
        }

        var ventures = CountOf(VentureId);
        var tickets = CountOf(AetheryteTicketId);
        var targetId = ventures <= tickets ? VentureId : AetheryteTicketId;

        spentCurrencyId = currencyId;
        currencyAtStart = seals;
        buyTargetItemId = targetId;
        targetAtStart = CountOf(targetId);

        Print($"{NameOf(currencyId)}s: {seals:N0}. Ventures {ventures:N0} vs tickets {tickets:N0} "
            + $"- buying {NameOf(targetId)}s.");

        var npcIds = centurio ? new[] { ArdolainDataId } : HuntBillmasterDataIds;
        var npcLabel = centurio ? "Ardolain" : "the hunt billmaster";
        var keywords = centurio
            ? new[] { "centurio seal", "seal", "exchange", "trade" }
            : new[] { "allied seal", "seal", "exchange", "trade" };

        Enqueue(() => InteractWith(npcIds, npcLabel), $"Interact with {npcLabel}");
        Enqueue(() => OpenShopMenu(keywords), "Open the seal shop");
        Enqueue(BuyFromCurrencyShop, $"Buy {NameOf(targetId)}s", 180000);
        Enqueue(CloseShopWindows, "Close the seal shop");
        Enqueue(ReportCurrencyRun, "Report");
    }

    /// <summary>Buys whichever Mathematics ware currently pays best per tomestone,
    /// judged from live market data rather than a fixed pick.</summary>
    internal void StartMaths()
    {
        if (!CanStart())
            return;

        var tomes = CurrencyCount(MathsTomeId);
        if (tomes < MathsUnitCost)
        {
            Print($"Only {tomes:N0} Mathematics tomestones - not enough for anything.");
            return;
        }

        var dataCenter = Svc.Data.GetExcelSheet<World>()
            .GetRowOrDefault(Svc.PlayerState.CurrentWorld.RowId)?
            .DataCenter.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(dataCenter))
        {
            Print("Couldn't work out your data centre.");
            return;
        }

        consultingMarket = true;
        Print($"Checking {dataCenter} prices for {tomes:N0} tomestones ({tomes / MathsUnitCost} items)...");

        Task.Run(async () =>
        {
            List<MarketAdvisor.Candidate> ranked;
            try
            {
                ranked = await MarketAdvisor.Rank(dataCenter, MathsWares, MathsUnitCost);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Laziness] Universalis lookup failed: {ex.Message}");
                await Svc.Framework.RunOnFrameworkThread(() =>
                {
                    consultingMarket = false;
                    Print("Couldn't reach Universalis - try again in a moment.");
                });
                return;
            }

            await Svc.Framework.RunOnFrameworkThread(() =>
            {
                consultingMarket = false;
                StartMathsPurchase(ranked);
            });
        });
    }

    private void StartMathsPurchase(List<MarketAdvisor.Candidate> ranked)
    {
        if (ranked.Count == 0)
        {
            Print("No market data for any of the Mathematics wares.");
            return;
        }

        foreach (var candidate in ranked)
            Print($"  {candidate.Name}: {candidate.Price:N0} gil, {candidate.UnitsPerDay:N0}/day sold "
                + $"=> {candidate.GilPerUnitCost:N0} gil per tomestone");

        var best = ranked[0];
        spentCurrencyId = MathsTomeId;
        currencyAtStart = CurrencyCount(MathsTomeId);
        buyTargetItemId = best.ItemId;
        targetAtStart = CountOf(best.ItemId);

        Print($"Buying {best.Name}.");

        Enqueue(() => InteractWith([ZirconDataId], "Zircon"), "Interact with Zircon");
        Enqueue(() => OpenShopMenu(["mathematics", "other", "material", "tomestone", "exchange"]), "Open Zircon's shop");
        Enqueue(BuyFromCurrencyShop, $"Buy {best.Name}", 180000);
        Enqueue(CloseShopWindows, "Close Zircon's shop");
        Enqueue(ReportCurrencyRun, "Report");
    }

    private bool CanStart()
    {
        if (Running)
        {
            Print("Already running - stop it first.");
            return false;
        }

        EzThrottler.Reset("Laziness.TabHint");
        return Svc.ClientState.IsLoggedIn;
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

    private bool? ReportSoilRun()
    {
        var spent = currencyAtStart - CurrencyCount(PoeticsItemId);
        var topsoil = Math.Max(CountOf(Grade3ShroudTopsoilId) - targetAtStart, 0);
        SetStatus($"Buy soil done: {topsoil} topsoil for {spent:N0} poetics.");
        Print($"Bought {shellsBought} Unidentifiable Shell and {topsoil} Grade 3 Shroud Topsoil for {spent:N0} poetics.");
        return true;
    }

    private bool? ReportCurrencyRun()
    {
        var spent = currencyAtStart - CurrencyCount(spentCurrencyId);
        var gained = Math.Max(CountOf(buyTargetItemId) - targetAtStart, 0);
        SetStatus($"Seal run done: {gained} x {NameOf(buyTargetItemId)} for {spent:N0}.");
        Print($"Bought {gained} {NameOf(buyTargetItemId)} for {spent:N0} {NameOf(spentCurrencyId)}s "
            + $"({CurrencyCount(spentCurrencyId):N0} left).");
        return true;
    }

    // ---- NPC interaction --------------------------------------------------------

    private unsafe bool? InteractWith(uint[] dataIds, string npcName)
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

        var npc = Svc.Objects.FirstOrDefault(o => dataIds.Contains(o.BaseId));
        if (npc == null)
        {
            SetStatus($"{npcName} isn't nearby.");
            Print($"{npcName} isn't nearby - stand next to them and try again.");
            return null; // aborts the queue
        }

        if (!EzThrottler.Throttle("Laziness.Interact", 2000))
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

    private static bool? SelectMenuEntry(string[] entries, string[] keywords, Action<int> select)
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

    /// <summary>Spends the shop's currency on <see cref="buyTargetItemId"/> until it
    /// runs out, the bags fill up, or the item can't be found in the window.</summary>
    private unsafe bool? BuyFromCurrencyShop()
    {
        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Yes", 500))
                yesno.Yes();
            return false;
        }

        if (!TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop) || !shop.IsAddonReady)
            return false;

        // The window only publishes the rows of the tab being displayed, and switching
        // tabs from code crashes this addon, so wait for the tab to be opened instead.
        // Buying resumes by itself the moment the item shows up.
        var entry = shop.BasicShopItems.FirstOrDefault(x => x.ItemId == buyTargetItemId);
        if (entry == null)
        {
            if (EzThrottler.Throttle("Laziness.TabHint", 10000))
            {
                SetStatus($"Waiting for the tab holding {NameOf(buyTargetItemId)}.");
                Print($"{NameOf(buyTargetItemId)} isn't on the tab that's showing - click the tab that has it "
                    + "(ventures and aetheryte tickets are under \"Others\") and I'll carry on.");
            }

            return false;
        }

        var cost = (int)Math.Max(entry.CostAmount, 1);
        var affordable = (int)shop.CurrencyAmount / cost;
        if (affordable <= 0)
        {
            SetStatus($"Currency spent ({shop.CurrencyAmount:N0} left, {cost} each).");
            return true;
        }

        if (!HasRoomFor(buyTargetItemId))
        {
            SetStatus("Inventory full.");
            Print($"No inventory room for more {NameOf(buyTargetItemId)} - stopping here.");
            return true;
        }

        var amount = Math.Min(affordable, MaxPerPurchase);
        if (!EzThrottler.Throttle("Laziness.Buy", 1000))
            return false;

        SetStatus($"Buying {amount} x {NameOf(buyTargetItemId)} at {cost} each...");
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
