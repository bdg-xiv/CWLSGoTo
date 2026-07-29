using Dalamud.Game.ClientState.Conditions;
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

    // Ryubool Ja trades Sacks of Nuts for Neo Kingdom gear; the halberd and the bow are
    // both carpentry desynth fodder, so they get bought and broken down in a loop.
    private const uint RyuboolJaDataId = 1048387;
    private const uint SackOfNutsId = 26533;
    private static readonly uint[] CrpFodderIds = [42699, 42700]; // Halberd, Composite Bow
    // A round is only one of each weapon now, so the cap has to be generous.
    private const int MaxCrpCycles = 200;

    // Grand Company quartermasters - all three, so it works whichever company you're in.
    private static readonly uint[] QuartermasterDataIds = [1002390, 1002387, 1002393];
    private static readonly uint[] GcSealIds = [22, 20, 21]; // Flame, Storm, Serpent
    private const uint GcCategoryTabNodeId = 44;             // + index; Materials is the 4th
    private const int GcMaterialsTabIndex = 3;

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
    private volatile bool gcLookupDone;
    private int purchaseUnitCap; // 0 = buy until the currency runs out
    private int crpCycle;
    private int crpBoughtThisCycle;
    private int crpBoughtTotal;
    private int crpUnitCost = 70;
    private bool crpStop;
    private bool crpDesynthedThisCycle;
    private long desynthSettleAt;
    private long gcStallDeadline;
    private int gcSealsAtLastPurchase;
    private GrandCompanyShop.Row? gcTargetRow;

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

    // UIColor 561 (#FA89B6) - a pink nothing else in chat uses, so these lines are
    // easy to pick out of a busy log.
    private const ushort TagColor = 561;

    /// <summary>Results go to the echo channel so they sit in the log with the rest
    /// of the plugin chatter rather than in Dalamud's debug channel.</summary>
    private static void Print(string message)
        => Svc.Chat.Print(new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = new SeStringBuilder().AddUiForeground($"[Laziness] {message}", TagColor).Build(),
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
        foreach (var type in (InventoryType[])[InventoryType.Currency, InventoryType.Crystals])
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    return (int)slot->Quantity;
            }
        }

        // Not every currency sits in the currency container - Centurio Seals don't -
        // so fall back to a whole-inventory count rather than reporting none.
        return manager->GetInventoryItemCount(itemId);
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
        // The hunt billmaster splits Allied Seals by category and keeps ventures and
        // tickets under "(Other)". Ardolain doesn't: he offers plain "Exchange Centurio
        // Seals" plus an "(Advanced)" counter of high-end gear, so take the plain one.
        var keywords = centurio
            ? new[] { "centurio+seals+!advanced", "centurio+seals", "exchange" }
            : new[] { "allied+other", "(other)", "other" };

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

        var world = SellingWorld();
        if (string.IsNullOrEmpty(world))
        {
            Print("Couldn't work out your home world.");
            return;
        }

        consultingMarket = true;
        Print($"Checking {world} prices for {tomes:N0} tomestones ({tomes / MathsUnitCost} items)...");

        Task.Run(async () =>
        {
            List<MarketAdvisor.Candidate> ranked;
            try
            {
                ranked = await MarketAdvisor.Rank(world, MathsWares, MathsUnitCost);
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

        // Rank by the gil actually collectable: a ware nobody buys looks fine per
        // tomestone and then sits unsold, so cap each option at what the market takes -
        // less whatever is already in your bags, retainers or listings, which is
        // competing for exactly the same buyers.
        var tomes = CurrencyCount(MathsTomeId);
        var options = ranked
            .Select(c =>
            {
                var held = Holdings.Owned(c.ItemId);
                var room = Math.Max(0, MarketAdvisor.AbsorbableUnits(c.UnitsPerDay) - held);
                return (Candidate: c, Held: held, Units: Math.Min(tomes / MathsUnitCost, room));
            })
            .Where(x => x.Units > 0)
            .OrderByDescending(x => (long)x.Units * x.Candidate.Price)
            .ToList();

        if (options.Count == 0)
        {
            Print("None of the Mathematics wares are worth buying right now - either they "
                + "don't sell fast enough, or you already hold a week of them.");
            return;
        }

        foreach (var option in options)
            Print($"  {option.Candidate.Name}: {option.Candidate.Price:N0} gil, {option.Candidate.UnitsPerDay:N0} sold/day, "
                + $"holding {option.Held:N0} => buy {option.Units:N0} for ~{option.Units * option.Candidate.Price * 0.95:N0} gil");

        var best = options[0].Candidate;
        spentCurrencyId = MathsTomeId;
        currencyAtStart = tomes;
        buyTargetItemId = best.ItemId;
        targetAtStart = CountOf(best.ItemId);
        purchaseUnitCap = options[0].Units;

        Print($"Buying {best.Name}.");

        Enqueue(() => InteractWith([ZirconDataId], "Zircon"), "Interact with Zircon");
        // Zircon lists a category per tomestone, so both words are needed - he sells
        // the other current tomestone's wares too.
        Enqueue(() => OpenShopMenu(["mathematics+other", "(other)"]), "Open Zircon's shop");
        Enqueue(BuyFromCurrencyShop, $"Buy {best.Name}", 180000);
        Enqueue(CloseShopWindows, "Close Zircon's shop");
        Enqueue(ReportCurrencyRun, "Report");
    }

    /// <summary>Spends company seals on whichever Materials row pays best per seal,
    /// judged the same way as the tomestone wares.</summary>
    internal void StartSeals()
    {
        if (!CanStart())
            return;

        var sealId = GcSealIds.OrderByDescending(CurrencyCount).First();
        var seals = CurrencyCount(sealId);
        if (seals <= 0)
        {
            Print("No company seals to spend.");
            return;
        }

        spentCurrencyId = sealId;
        currencyAtStart = seals;
        buyTargetItemId = 0;
        gcTargetRow = null;
        gcLookupDone = false;

        Print($"{NameOf(sealId)}s: {seals:N0}. Checking the Materials counter...");

        Enqueue(() => InteractWith(QuartermasterDataIds, "a quartermaster"), "Interact with the quartermaster");
        Enqueue(() => OpenShopMenu(["purchase", "exchange", "trade"]), "Open the seal exchange");
        Enqueue(WaitForMaterialsTab, "Wait for the Materials tab", 120000);
        Enqueue(StartGcMarketLookup, "Read the Materials rows");
        // false keeps waiting; null would abort the whole queue.
        Enqueue(() => gcLookupDone, "Wait for prices", 60000);
        Enqueue(BuyGcMaterial, "Buy the best material", 300000);
        Enqueue(CloseGcExchange, "Close the exchange");
        Enqueue(ReportCurrencyRun, "Report");
    }

    private static unsafe bool GcExchangeOpen(out AtkUnitBase* addon)
        => TryGetAddonByName("GrandCompanyExchange", out addon) && IsAddonReady(addon);

    /// <summary>Waits for the Materials category to be showing. Clicking the tab from
    /// code doesn't take on this window, so its state is only read, never driven.</summary>
    private unsafe bool? WaitForMaterialsTab()
    {
        if (!GcExchangeOpen(out var addon))
            return false;

        var node = addon->GetNodeById(GcCategoryTabNodeId + GcMaterialsTabIndex);
        var radio = node != null ? node->GetAsAtkComponentRadioButton() : null;
        if (radio != null && radio->IsSelected)
            return true;

        if (EzThrottler.Throttle("Laziness.GcTabHint", 8000))
            Print("Click the Materials tab and I'll carry on.");

        return false;
    }

    /// <summary>Reads what the Materials tab is offering and asks the market which of
    /// those tradeable rows pays best per seal.</summary>
    private unsafe bool? StartGcMarketLookup()
    {
        if (!GcExchangeOpen(out var addon))
            return false;

        var rows = new GrandCompanyShop(addon).Rows();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        var tradeable = rows
            .Where(r => sheet.GetRowOrDefault(r.ItemId) is { IsUntradable: false })
            .ToList();

        if (tradeable.Count == 0)
        {
            Print("Nothing tradeable on the Materials tab - is it the tab that's showing?");
            return null;
        }

        var world = SellingWorld();
        if (string.IsNullOrEmpty(world))
        {
            Print("Couldn't work out your home world.");
            return null;
        }

        SetStatus($"Pricing {tradeable.Count} Materials rows on {world}...");
        LaunchGcMarketLookup(world, tradeable);
        return true;
    }

    /// <summary>The world these items would actually be sold on. Market boards are per
    /// world, and retainers stay on the home world even while visiting another.</summary>
    private static string? SellingWorld()
        => Svc.Data.GetExcelSheet<World>().GetRowOrDefault(Svc.PlayerState.HomeWorld.RowId)?.Name.ExtractText();

    /// <summary>The await has to live outside the unsafe row-reading code.</summary>
    private void LaunchGcMarketLookup(string dataCenter, List<GrandCompanyShop.Row> rows)
    {
        var names = rows.ToDictionary(r => r.ItemId, r => NameOf(r.ItemId));
        var costs = rows.ToDictionary(r => r.ItemId, r => (int)r.SealCost);

        Task.Run(async () =>
        {
            List<MarketAdvisor.Candidate> ranked;
            try
            {
                // Seal costs differ per row, so rank on a flat unit cost and divide by
                // each row's own cost afterwards.
                ranked = await MarketAdvisor.Rank(dataCenter, names, 1);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[Laziness] Universalis lookup failed: {ex.Message}");
                await Svc.Framework.RunOnFrameworkThread(() => Print("Couldn't reach Universalis - try again in a moment."));
                return;
            }

            await Svc.Framework.RunOnFrameworkThread(() =>
            {
                var seals = CurrencyCount(spentCurrencyId);

                // Rank by the gil actually collectable, not the rate: a cheap row with
                // no buyers looks great per seal and then sits in your bags forever.
                var best = ranked
                    .Select(c =>
                    {
                        var cost = Math.Max(costs[c.ItemId], 1);
                        // Stock already held - bags, retainers, and anything still sitting
                        // on the board - eats into the same week of demand.
                        var held = Holdings.Owned(c.ItemId);
                        var room = Math.Max(0, MarketAdvisor.AbsorbableUnits(c.UnitsPerDay) - held);
                        var units = Math.Min(seals / cost, room);
                        return (Candidate: c, Cost: cost, Held: held, Units: units, Gil: units * c.Price * 0.95);
                    })
                    .Where(x => x.Units > 0)
                    .OrderByDescending(x => x.Gil)
                    .ToList();

                if (best.Count == 0)
                {
                    Print("Nothing on the Materials tab is worth buying - either it doesn't "
                        + "sell fast enough, or you already hold a week of it.");
                    return;
                }

                foreach (var option in best.Take(5))
                    Print($"  {option.Candidate.Name}: {option.Candidate.Price:N0} gil / {option.Cost:N0} seals, "
                        + $"{option.Candidate.UnitsPerDay:N0} sold/day, holding {option.Held:N0} "
                        + $"=> buy {option.Units:N0} for ~{option.Gil:N0} gil");

                var winner = best[0];
                buyTargetItemId = winner.Candidate.ItemId;
                targetAtStart = CountOf(winner.Candidate.ItemId);
                gcTargetRow = rows.First(r => r.ItemId == winner.Candidate.ItemId);
                purchaseUnitCap = winner.Units;

                var sealsUsed = winner.Units * winner.Cost;
                Print($"Buying {winner.Units:N0} x {winner.Candidate.Name} ({sealsUsed:N0} seals) - "
                    + $"that's about a week of sales, so the rest stays as seals.");
                gcLookupDone = true;
            });
        });
    }

    private unsafe bool? BuyGcMaterial()
    {
        if (gcTargetRow == null)
            return true;

        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Yes", 500))
                yesno.Yes();
            return false;
        }

        if (!GcExchangeOpen(out var addon))
            return false;

        var seals = CurrencyCount(spentCurrencyId);
        var cost = (int)Math.Max(gcTargetRow.SealCost, 1);
        var affordable = seals / cost;
        if (affordable <= 0)
        {
            SetStatus($"Seals spent ({seals:N0} left, {cost} each).");
            return true;
        }

        var stillWanted = UnitsLeftToBuy();
        if (stillWanted <= 0)
        {
            SetStatus("Bought what the market can take.");
            return true;
        }

        if (!HasRoomFor(buyTargetItemId))
        {
            Print($"No inventory room for more {NameOf(buyTargetItemId)} - stopping here.");
            return true;
        }

        // Stall guard: if the seals stop going down the purchase isn't landing (rank
        // requirement, a dialog we don't handle), so stop instead of looping.
        var now = Environment.TickCount64;
        if (seals != gcSealsAtLastPurchase)
        {
            gcSealsAtLastPurchase = seals;
            gcStallDeadline = now + 15000;
        }
        else if (now > gcStallDeadline && gcStallDeadline != 0)
        {
            Print($"{NameOf(buyTargetItemId)} isn't going through - stopping.");
            return true;
        }

        if (!EzThrottler.Throttle("Laziness.BuyGc", 1000))
            return false;

        var amount = Math.Min(Math.Min(affordable, MaxPerPurchase), stillWanted);
        SetStatus($"Buying {amount} x {NameOf(buyTargetItemId)} at {cost} seals...");
        new GrandCompanyShop(addon).Buy(gcTargetRow, amount);
        return false;
    }

    /// <summary>How many more units to buy before the market-absorption cap is hit.
    /// int.MaxValue when the run isn't capped (ventures, tickets, shells).</summary>
    private int UnitsLeftToBuy()
        => purchaseUnitCap <= 0
            ? int.MaxValue
            : purchaseUnitCap - Math.Max(CountOf(buyTargetItemId) - targetAtStart, 0);

    private static unsafe bool? CloseGcExchange()
    {
        if (!GcExchangeOpen(out var addon))
            return true;

        if (EzThrottler.Throttle("Laziness.CloseGc", 500))
            addon->Close(true);
        return false;
    }

    /// <summary>Buys Neo Kingdom halberds and bows until the bags are full, desynthesizes
    /// the lot, and goes round again until the nuts run out.</summary>
    internal void StartCrp()
    {
        if (!CanStart())
            return;

        var nuts = CurrencyCount(SackOfNutsId);
        if (nuts < crpUnitCost)
        {
            Print($"Only {nuts:N0} Sacks of Nuts - not enough for a weapon.");
            return;
        }

        spentCurrencyId = SackOfNutsId;
        currencyAtStart = nuts;
        crpCycle = 0;
        crpBoughtTotal = 0;
        crpStop = false;

        Print($"Sacks of Nuts: {nuts:N0}. Buying halberds and bows to desynthesize.");
        EnqueueCrpCycle();
    }

    private void EnqueueCrpCycle()
    {
        crpCycle++;
        crpBoughtThisCycle = 0;
        crpDesynthedThisCycle = false;
        desynthSettleAt = 0;

        Enqueue(() => InteractWith([RyuboolJaDataId], "Ryubool Ja"), "Interact with Ryubool Ja");
        Enqueue(() => OpenShopMenu(["neo kingdom+dow", "neo+dow"]), "Open the Neo Kingdom (DoW) counter");
        Enqueue(BuyCrpFodder, "Buy halberds and bows", 300000);
        Enqueue(CloseShopWindows, "Close the shop");
        Enqueue(RunDesynthAll, "Run /desynthall");
        Enqueue(WaitForDesynth, "Wait for desynthesis", 900000);
        Enqueue(CrpCycleEnd, "Round finished");
    }

    /// <summary>Buys the two weapons one at a time, keeping them even, until the nuts or
    /// the bag space run out. They don't stack, so each one needs its own slot.</summary>
    private unsafe bool? BuyCrpFodder()
    {
        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            if (EzThrottler.Throttle("Laziness.Yes", 400))
                yesno.Yes();
            return false;
        }

        if (!TryGetAddonMaster<AddonMaster.ShopExchangeCurrency>("ShopExchangeCurrency", out var shop) || !shop.IsAddonReady)
            return false;

        var rows = shop.BasicShopItems.Where(x => CrpFodderIds.Contains(x.ItemId)).ToList();
        if (rows.Count == 0)
        {
            if (EzThrottler.Throttle("Laziness.TabHint", 8000))
                Print("The halberd and bow aren't on the tab that's showing - click the Weapons tab.");
            return false;
        }

        // These weapons are unique - the game refuses a second copy - so a round buys
        // one of each and the desynthesis afterwards makes room for the next pair.
        var sheet = Svc.Data.GetExcelSheet<Item>();
        var next = rows.FirstOrDefault(r =>
            !(sheet.GetRowOrDefault(r.ItemId)?.IsUnique ?? false) || CountOf(r.ItemId) == 0);

        if (next == null)
        {
            SetStatus("Holding one of each - time to desynthesize.");
            return true;
        }

        crpUnitCost = (int)Math.Max(next.CostAmount, 1);

        if (shop.CurrencyAmount < next.CostAmount)
        {
            SetStatus($"Nuts spent ({shop.CurrencyAmount:N0} left).");
            return true;
        }

        if (!HasRoomFor(next.ItemId))
        {
            SetStatus("Bags full - time to desynthesize.");
            return true;
        }

        if (!EzThrottler.Throttle("Laziness.BuyCrp", 600))
            return false;

        crpBoughtThisCycle++;
        crpBoughtTotal++;
        SetStatus($"Buying {NameOf(next.ItemId)} ({crpBoughtThisCycle} this round)...");
        next.Select(1);
        return false;
    }

    private bool? RunDesynthAll()
    {
        // Desynthesize whatever fodder is on hand, including copies bought before the
        // run started - not only what this round managed to buy.
        crpDesynthedThisCycle = CrpFodderIds.Sum(CountOf) > 0;
        if (!crpDesynthedThisCycle)
            return true;

        if (!Svc.Commands.Commands.ContainsKey("/desynthall"))
        {
            Print("The DesynthAll plugin isn't installed - stopping with the weapons in your bags.");
            crpStop = true;
            return true;
        }

        Print($"Bought {crpBoughtThisCycle} this round - desynthesizing.");
        Svc.Commands.ProcessCommand("/desynthall");
        return true;
    }

    /// <summary>Waits for the desynthesis run to eat the weapons. If its windows close
    /// with weapons still in the bags, its own filters skipped them - say so and stop
    /// rather than buying another round that would also sit there.</summary>
    private unsafe bool? WaitForDesynth()
    {
        if (!crpDesynthedThisCycle || crpStop)
            return true;

        var left = CrpFodderIds.Sum(CountOf);
        if (left == 0)
            return true;

        var working = (TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var selector) && IsAddonReady(selector))
            || TryGetAddonByName<AtkUnitBase>("SalvageDialog", out _)
            || TryGetAddonByName<AtkUnitBase>("SalvageResult", out _)
            || Svc.Condition[ConditionFlag.Occupied39];

        var now = Environment.TickCount64;
        if (working)
        {
            desynthSettleAt = now + 5000;
            return false;
        }

        if (desynthSettleAt == 0)
        {
            desynthSettleAt = now + 5000;
            return false;
        }

        if (now < desynthSettleAt)
            return false;

        Print($"{left} weapon(s) weren't desynthesized - check DesynthAll's \"only items that grant skill\" setting.");
        crpStop = true;
        return true;
    }

    private bool? CrpCycleEnd()
    {
        var nuts = CurrencyCount(SackOfNutsId);
        var progressed = crpBoughtThisCycle > 0 || crpDesynthedThisCycle;
        var more = !crpStop && progressed && nuts >= crpUnitCost && crpCycle < MaxCrpCycles;
        if (more)
        {
            EnqueueCrpCycle();
            return true;
        }

        var reason = crpStop ? "stopped"
            : nuts < crpUnitCost ? "out of nuts"
            : !progressed ? "nothing more could be bought"
            : "round limit reached";
        Print($"Done ({reason}). Bought and desynthesized {crpBoughtTotal} weapon(s) over {crpCycle} round(s); "
            + $"{nuts:N0} Sacks of Nuts left.");
        return true;
    }

    private bool CanStart()
    {
        if (Running)
        {
            Print("Already running - stop it first.");
            return false;
        }

        EzThrottler.Reset("Laziness.TabHint");
        purchaseUnitCap = 0;
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
        || (TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var item) && IsAddonReady(item))
        || GcExchangeOpen(out _);

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

        // A keyword may require several words at once ("centurio+seals") and rule words
        // out with "!" ("!advanced"), which is how the right entry is picked from menus
        // that list near-identical options.
        foreach (var keyword in keywords)
        {
            var tokens = keyword.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var required = tokens.Where(t => !t.StartsWith('!')).ToArray();
            var banned = tokens.Where(t => t.StartsWith('!')).Select(t => t[1..]).ToArray();
            var index = Array.FindIndex(entries, e =>
                required.All(part => e.Contains(part, StringComparison.OrdinalIgnoreCase))
                && !banned.Any(part => e.Contains(part, StringComparison.OrdinalIgnoreCase)));
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

        var stillWanted = UnitsLeftToBuy();
        if (stillWanted <= 0)
        {
            SetStatus("Bought what the market can take.");
            return true;
        }

        var amount = Math.Min(Math.Min(affordable, MaxPerPurchase), stillWanted);
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
