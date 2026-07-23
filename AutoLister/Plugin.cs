using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.Utility;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static ECommons.GenericHelpers;
using Callback = ECommons.Automation.Callback;

namespace AutoLister;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/autolist";

    private const int RetainerSellSlots = 20;
    private const int UndercutGil = 1;

    // The market board caps a single listing at 99 units regardless of bag stack size.
    private const int MaxItemsPerListing = 99;

    // Below this total (price x stack quantity) an item is vendored through the
    // retainer instead of being listed on the market.
    private const long VendorThresholdGil = 2000;

    // Market proceeds lose roughly this much to the sales tax; vendoring pays face
    // value, so the comparison is vendor total vs. listing total after tax.
    private const int MarketTaxPercent = 5;

    // Price-crash guard for Pinch & Cull: when a listing priced above the floor would
    // have to drop by at least this much to undercut, the market has probably been
    // crashed - pull the item back to the bags and wait it out instead of chasing down.
    private const int PriceCrashFloorGil = 50_000;
    private const int PriceCrashDropPercent = 30;
    private const char HqGlyph = (char)0xE03C;

    // Event codes lifted from Dagobert's auto pinch, which drives the same addons.
    private const int RetainerSellConfirmEvent = 0;
    private const int RetainerSellCancelEvent = 1;
    private const int RetainerSellComparePricesEvent = 4;

    // A market board offerings response carries at most 10 listings; when a batch is
    // full and had no HQ match, more batches may still arrive for the same request.
    private const int ListingsPerBatch = 10;
    private const int MarketBoardResultTimeoutMs = 5000;

    // Dagobert's pacing (GetMBPricesDelayMS / MarketBoardKeepOpenMS defaults): wait
    // before clicking Compare Prices - the results sometimes don't populate without
    // it - and keep the results window open a moment before reading the price.
    private const int MbOpenDelayMs = 3000;
    private const int MbKeepOpenMs = 1000;

    private readonly TaskManager taskManager;
    private readonly Configuration config;

    private enum RunMode { List, Pinch }

    private readonly Queue<(InventoryType Container, int Slot, uint ItemId)> pendingItems = new();
    private readonly Dictionary<string, int> cachedPrices = [];
    private bool skipCurrentItem;
    private bool vendorCurrentItem;
    private long compareOpenAt;
    private InventoryType currentContainer;
    private int currentSlot;
    private uint currentItemId;
    private int listedCount;
    private int skippedCount;
    private int vendoredCount;
    private readonly List<(string Name, int Price, string Change)> listedReport = [];
    private readonly List<string> manualPricingReport = [];

    // Stack-merge state: when the current retainer already sells the (stackable)
    // item, that listing is pulled back with "Return Items to Inventory" (straight
    // to the player's bags, where the stacks combine) and the merged stack goes up
    // as one listing.
    private bool mergePending;
    private bool mergeCompleted;
    private int mergeMarketCountBefore;
    private string mergeItemName = "";
    private MergeStep mergeStep;
    private int mergeProbeRow;
    private int mergeRowCount;
    private long mergeStepDeadline;
    private int mergeBagQuantity;
    private int mergeMaxPerListing;
    private const int MergeStepTimeoutMs = 4000;
    private readonly List<(string Name, string Retainer)> setAsideReport = [];

    private enum MergeStep { OpenProbeMenu, ClickAdjust, ReadWindow, ReopenMenu, ClickReturn, WaitReturned }

    private enum MergeAvail { None, Full, Mergeable }

    // Cross-retainer follow-up: set-aside items grouped by the retainer that already
    // sells them; after the main run the plugin swaps retainers and merges there.
    private readonly List<(uint ItemId, bool Hq, ulong RetainerId, string RetainerName)> followUpItems = [];
    private readonly Queue<(ulong RetainerId, string RetainerName, List<(uint ItemId, bool Hq)> Items)> followUpQueue = new();
    private List<(uint ItemId, bool Hq)> currentFollowUpItems = [];
    private bool followUpPhase;

    private bool currentItemHq;

    // Pinch & Cull state: walk the retainer's existing listings, reprice the healthy
    // ones and delist+vendor the ones under the thresholds.
    private RunMode runMode = RunMode.List;
    private bool allRetainersMode;
    private readonly Queue<uint> retainerQueue = new();
    private readonly Queue<int> pendingListingIndexes = new();
    private bool cullCurrentItem;
    private bool crashKeepItem;
    private bool currentHadBagCopy;
    private long returnLandedAt;
    private string currentItemName = "";
    private string crashDetail = "";
    private int currentListingIndex;
    private int currentQuantity;
    private int marketCountBeforeReturn;
    private int repricedCount;
    private readonly List<string> culledReport = [];
    private readonly List<string> returnedReport = [];
    private readonly List<(string Name, string Detail)> crashedReport = [];
    private static Dictionary<string, uint>? itemIdsByName;

    // Market board price request state, mirroring Dagobert's MarketBoardHandler.
    private bool newRequest;
    private bool itemIsHq;
    private bool useHq;
    private int lastRequestId = -1;
    private int? newPrice;
    private int pendingNoMatchRequestId = -1;
    private long pendingNoMatchTimeoutAt;
    private long pendingNoOfferingsTimeoutAt;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // TaskManager's constructor hooks Svc.Framework.Update, so it must be created
        // after ECommonsMain.Init - a field initializer would run too early and throw.
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = true });

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Lists every sellable item from the bottom-right inventory quarter on the market (same as the Auto List button)."
        });

        Svc.MarketBoard.OfferingsReceived += OnOfferingsReceived;
        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellPostSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultPostSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellListPostSetup);
        PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawOverlay;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellPostSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultPostSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSellList", OnRetainerSellListPostSetup);
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
        taskManager.Abort();
        Svc.Commands.RemoveHandler(CommandName);
        taskManager.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args) => StartListing();

    #region Overlay button

    private unsafe void DrawOverlay()
    {
        DrawSellListOverlay();
        DrawRetainerListOverlay();
    }

    private unsafe void DrawSellListOverlay()
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon) || !addon->IsVisible)
            return;

        // Same anchor node Dagobert overlays its Auto Pinch button on; ours goes to its left.
        var node = addon->UldManager.NodeList[17];
        if (node == null)
            return;

        var pos = GetNodePosition(node);
        var scale = GetNodeScale(node);

        var totalWidth = taskManager.IsBusy
            ? EstimateButtonWidth("Cancel", scale.X)
            : EstimateButtonWidth("Auto List", scale.X) + EstimateButtonWidth("Pinch & Cull", scale.X);

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(pos.X - totalWidth - 4f * scale.X, pos.Y));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.Begin("###AutoListerButton",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);

        if (taskManager.IsBusy)
        {
            if (ImGui.Button("Cancel"))
                CancelListing();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancels the current run");
        }
        else
        {
            if (ImGui.Button("Auto List"))
                StartListing();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Puts every sellable item from the bottom-right inventory quarter up for sale,\nundercutting the cheapest matching HQ/NQ listing by 1 gil.\nPlease do not interact with the game while this runs.");
            ImGui.SameLine();
            if (ImGui.Button("Pinch & Cull"))
                StartPinch(allRetainersRun: false);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reprices this retainer's listings like Auto Pinch, but items whose new\nprice totals under 2,000 gil (or that the vendor pays more for) are\ndelisted and sold to the vendor instead.\nPlease do not interact with the game while this runs.");
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    private unsafe void DrawRetainerListOverlay()
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !IsAddonReady(addon) || !addon->IsVisible)
            return;

        // Same anchor node Dagobert uses for its retainer-list Auto Pinch button.
        var node = addon->UldManager.NodeList[27];
        if (node == null)
            return;

        var pos = GetNodePosition(node);
        var scale = GetNodeScale(node);
        var label = taskManager.IsBusy ? "Cancel" : "Pinch & Cull All";
        var width = EstimateButtonWidth(label, scale.X);

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(pos.X - width - 4f * scale.X, pos.Y));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.Begin("###AutoListerRetainerListButton",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);

        if (taskManager.IsBusy)
        {
            if (ImGui.Button("Cancel"))
                CancelListing();
        }
        else
        {
            if (ImGui.Button("Pinch & Cull All"))
                StartPinch(allRetainersRun: true);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Opens every retainer with market listings in turn and runs Pinch & Cull\non each: repricing healthy listings, delisting + vendoring the ones under\nthe thresholds. Please do not interact with the game while this runs.");
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    private static float EstimateButtonWidth(string label, float scale)
        => (ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 12f) * scale;

    private static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
        var pos = new Vector2(node->X, node->Y);
        for (var parent = node->ParentNode; parent != null; parent = parent->ParentNode)
        {
            pos *= new Vector2(parent->ScaleX, parent->ScaleY);
            pos += new Vector2(parent->X, parent->Y);
        }

        return pos;
    }

    private static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
        var scale = new Vector2(node->ScaleX, node->ScaleY);
        for (var parent = node->ParentNode; parent != null; parent = parent->ParentNode)
            scale *= new Vector2(parent->ScaleX, parent->ScaleY);

        return scale;
    }

    #endregion

    #region Listing flow

    private unsafe void StartListing()
    {
        if (taskManager.IsBusy)
            return;

        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
        {
            Svc.Chat.Print("[AutoLister] Open a retainer's sell list (Markets window) first.");
            return;
        }

        ResetRunState();

        // The inventory the player sees is display-sorted: the visible grid is defined
        // by ItemOrderModule, not by the physical Inventory1-4 containers (an item shown
        // bottom-right can physically live in any container). The "bottom right quarter"
        // is therefore the LAST DISPLAY PAGE of the sorter, and each sorter entry maps
        // that display position back to its physical container page + slot.
        var sorter = ItemOrderModule.Instance()->InventorySorter;
        if (sorter == null || sorter->ItemsPerPage <= 0)
            return;

        var manager = InventoryManager.Instance();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        var totalSlots = sorter->Items.LongCount;
        var start = totalSlots - sorter->ItemsPerPage;

        for (var i = Math.Max(start, 0); i < totalSlots; i++)
        {
            var entry = sorter->Items[i].Value;
            if (entry == null)
                continue;

            var container = (InventoryType)((int)sorter->InventoryType + entry->Page);
            var containerPtr = manager->GetInventoryContainer(container);
            var inventorySlot = containerPtr != null ? containerPtr->GetInventorySlot(entry->Slot) : null;
            if (inventorySlot == null || inventorySlot->ItemId == 0)
                continue;

            // Marketable items have a search category; everything else can never be
            // listed. Items that are bound anyway get skipped later when their context
            // menu turns out to have no "Put Up for Sale" entry.
            var item = sheet.GetRowOrDefault(inventorySlot->ItemId);
            if (item == null || item.Value.ItemSearchCategory.RowId == 0)
                continue;

            pendingItems.Enqueue((container, entry->Slot, inventorySlot->ItemId));
        }

        if (pendingItems.Count == 0)
        {
            Svc.Chat.Print("[AutoLister] No sellable items in the bottom-right inventory quarter.");
            return;
        }

        Svc.Chat.Print($"[AutoLister] Listing up to {pendingItems.Count} item(s)...");
        taskManager.Enqueue(ProcessNextItem, "ProcessNextItem");
    }

    private void CancelListing()
    {
        taskManager.Abort();
        pendingItems.Clear();
        pendingListingIndexes.Clear();
        retainerQueue.Clear();
        skipCurrentItem = false;
        newRequest = false;
        mergePending = false;
        followUpQueue.Clear();
        followUpPhase = false;
        SuppressAutoRetainer(false);
        Svc.Chat.Print("[AutoLister] Cancelled.");
        PrintReport();
    }

    private static void SuppressAutoRetainer(bool value)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(value);
        }
        catch
        {
            // AutoRetainer not installed - nothing to suppress.
        }
    }

    private void ResetRunState()
    {
        pendingItems.Clear();
        cachedPrices.Clear();
        skipCurrentItem = false;
        vendorCurrentItem = false;
        compareOpenAt = 0;
        listedCount = 0;
        skippedCount = 0;
        vendoredCount = 0;
        listedReport.Clear();
        manualPricingReport.Clear();
        setAsideReport.Clear();
        mergePending = false;
        mergeCompleted = false;
        followUpItems.Clear();
        followUpQueue.Clear();
        currentFollowUpItems = [];
        followUpPhase = false;
        runMode = RunMode.List;
        allRetainersMode = false;
        retainerQueue.Clear();
        pendingListingIndexes.Clear();
        cullCurrentItem = false;
        crashKeepItem = false;
        repricedCount = 0;
        culledReport.Clear();
        returnedReport.Clear();
        crashedReport.Clear();
        newRequest = false;
        newPrice = null;
        lastRequestId = -1;
        pendingNoMatchRequestId = -1;
        pendingNoMatchTimeoutAt = 0;
        pendingNoOfferingsTimeoutAt = 0;
    }

    private unsafe bool? ProcessNextItem()
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
        {
            Svc.Chat.Print("[AutoLister] Sell list closed, stopping.");
            PrintReport();
            return null; // Aborts the queue.
        }

        var retainer = RetainerManager.Instance()->GetActiveRetainer();
        if (retainer == null)
        {
            Svc.Chat.Print("[AutoLister] No active retainer, stopping.");
            PrintReport();
            return null;
        }

        // A merge pull that never completed leaves the flag set; surface it - the
        // old listing is still up and only the bag stack would have been listed.
        if (mergePending)
        {
            mergePending = false;
            crashedReport.Add((mergeItemName, "could not pull its old listing - not merged"));
            Svc.Chat.Print($"[AutoLister] {mergeItemName}: could not pull the existing listing back - not merged.");
        }

        var slotsFull = retainer->MarketItemCount >= RetainerSellSlots;

        // Pull the next pending item that is still sitting in its physical slot.
        // A stackable item the current retainer already sells is handled as a merge
        // (return the listing, list the combined stack - works even at 20/20 slots);
        // one another retainer sells is set aside to be stacked over there instead.
        var manager = InventoryManager.Instance();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        var found = false;
        InventoryType itemContainer = default;
        var slot = -1;
        var pendingItemId = 0u;
        var pendingHq = false;
        var pendingBagQty = 1;
        var pendingMaxPerListing = MaxItemsPerListing;
        var mergeHere = false;
        while (pendingItems.Count > 0)
        {
            var candidate = pendingItems.Dequeue();
            var containerPtr = manager->GetInventoryContainer(candidate.Container);
            var inventorySlot = containerPtr != null ? containerPtr->GetInventorySlot(candidate.Slot) : null;
            if (inventorySlot == null || inventorySlot->ItemId != candidate.ItemId)
                continue;

            var hq = inventorySlot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var stackSize = (int)(sheet.GetRowOrDefault(candidate.ItemId)?.StackSize ?? 1);
            var stackable = stackSize > 1;
            var bagQty = Math.Max((int)inventorySlot->Quantity, 1);
            var maxPerListing = Math.Min(MaxItemsPerListing, stackSize);
            var avail = stackable ? CheckActiveRetainerListings(candidate.ItemId, hq, bagQty, maxPerListing) : MergeAvail.None;
            var mergeableHere = avail == MergeAvail.Mergeable;

            if (slotsFull && !mergeableHere)
                continue; // full retainer: only merges into existing listings still fit

            // Existing listings with no room (a full 99 stack) can't absorb anything:
            // the item goes up as a NEW stack instead of attempting a merge.
            if (avail == MergeAvail.Full)
                Svc.Chat.Print($"[AutoLister] {ItemNameOf(candidate.ItemId)}: the existing listing has no room - putting up a new stack.");

            // A follow-up visit exists only to consolidate at this retainer; if the
            // expected listing is gone entirely (sold, stale snapshot), leave the item
            // in the bags rather than listing it on a retainer it was never meant for.
            if (followUpPhase && avail == MergeAvail.None)
            {
                Svc.Chat.Print($"[AutoLister] {ItemNameOf(candidate.ItemId)}: no live listing to merge with here - leaving it in your bags.");
                continue;
            }

            if (!followUpPhase && !mergeableHere && stackable && avail == MergeAvail.None
                && TryFindOtherRetainerListing(candidate.ItemId, hq, out var otherRetainerId, out var otherRetainer))
            {
                var setAsideName = ItemNameOf(candidate.ItemId);
                setAsideReport.Add((setAsideName, otherRetainer));
                followUpItems.Add((candidate.ItemId, hq, otherRetainerId, otherRetainer));
                Svc.Chat.Print($"[AutoLister] {setAsideName}: already listed by {otherRetainer} - will stack it there afterwards.");
                continue;
            }

            itemContainer = candidate.Container;
            slot = candidate.Slot;
            pendingItemId = candidate.ItemId;
            pendingHq = hq;
            pendingBagQty = bagQty;
            pendingMaxPerListing = maxPerListing;
            mergeHere = mergeableHere;
            found = true;
            break;
        }

        if (!found)
        {
            FinishRun(slotsFull ? "the retainer's sell slots are full" : "no sellable items left in the bottom-right quarter");
            return true;
        }

        skipCurrentItem = false;
        vendorCurrentItem = false;
        mergeCompleted = false;
        mergePending = false;
        newPrice = null;
        compareOpenAt = 0;
        currentContainer = itemContainer;
        currentSlot = slot;
        currentItemId = pendingItemId;
        currentItemHq = pendingHq;

        if (mergeHere)
        {
            // The sell list's row order is a category sort, not the market container
            // order, so the right row can only be identified by opening each row's
            // price window and reading the item off it - the state machine probes
            // rows, verifies by resolved item id + HQ flag, and only then clicks
            // "Return Items to Inventory" (which has no confirmation of its own).
            mergePending = true;
            mergeStep = MergeStep.OpenProbeMenu;
            mergeProbeRow = 0;
            mergeRowCount = SellListRowCount();
            mergeMarketCountBefore = retainer->MarketItemCount;
            mergeItemName = ItemNameOf(pendingItemId);
            mergeBagQuantity = pendingBagQty;
            mergeMaxPerListing = pendingMaxPerListing;
            mergeStepDeadline = Environment.TickCount64 + MergeStepTimeoutMs;
            Svc.Chat.Print($"[AutoLister] {mergeItemName}: this retainer already sells it - pulling that listing so both stacks go up as one.");
            taskManager.Enqueue(MergeLocateAndPull, "MergeLocateAndPull", new TaskManagerConfiguration { TimeLimitMS = 90000, AbortOnTimeout = false, TimeoutSilently = true });
            // "Return Items to Inventory" drops the stack straight into the bags,
            // where it combines with the pending stack; give it a beat to land.
            taskManager.EnqueueDelay(600);
        }

        taskManager.Enqueue(() => OpenItemContextMenu(itemContainer, slot), "OpenItemContextMenu");
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(ClickPutUpForSale, "ClickPutUpForSale", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(RequestPrice, "RequestPrice", new TaskManagerConfiguration { TimeLimitMS = MbOpenDelayMs + 5000, AbortOnTimeout = false, TimeoutSilently = true });
        // Dagobert keeps the results window open for a bit before reading the price.
        taskManager.EnqueueDelay(MbKeepOpenMs);
        taskManager.Enqueue(SetPriceAndConfirm, "SetPriceAndConfirm", new TaskManagerConfiguration { TimeLimitMS = MarketBoardResultTimeoutMs + 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(200);
        taskManager.Enqueue(Cleanup, "Cleanup", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        // Vendor path: only acts when SetPriceAndConfirm flagged the item as too cheap to list.
        taskManager.Enqueue(VendorViaRetainer, "VendorViaRetainer", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(ClickHaveRetainerSell, "ClickHaveRetainerSell", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(WaitVendorComplete, "WaitVendorComplete", new TaskManagerConfiguration { TimeLimitMS = 4000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(400);
        taskManager.Enqueue(ProcessNextItem, "ProcessNextItem");
        return true;
    }

    private void FinishRun(string reason)
    {
        RefreshActiveRetainerSnapshot();

        // Items set aside because another retainer sells them: swap to that retainer
        // and merge there before wrapping up.
        if (runMode == RunMode.List)
        {
            if (followUpItems.Count > 0)
            {
                foreach (var group in followUpItems.GroupBy(f => f.RetainerId))
                    followUpQueue.Enqueue((group.Key, group.First().RetainerName, group.Select(f => (f.ItemId, f.Hq)).ToList()));
                followUpItems.Clear();
            }

            if (StartNextFollowUp(reason))
                return;
        }

        followUpPhase = false;
        SuppressAutoRetainer(false);
        var summary = runMode == RunMode.Pinch
            ? $"[AutoLister] Done ({reason}). Repriced {repricedCount} listing(s)."
            : $"[AutoLister] Done ({reason}). Listed {listedCount} item(s).";
        if (vendoredCount > 0)
            summary += $" Vendored {vendoredCount}.";
        if (setAsideReport.Count > 0)
            summary += $" Set aside {setAsideReport.Count} (listed elsewhere).";
        if (crashedReport.Count > 0)
            summary += $" Pulled {crashedReport.Count} (price crash).";
        if (returnedReport.Count > 0)
            summary += $" Returned {returnedReport.Count} to bags.";
        if (skippedCount > 0)
            summary += $" Skipped {skippedCount}.";
        Svc.Chat.Print(summary);
        PrintReport();
    }

    /// <summary>Closes the current retainer and opens the next one holding set-aside
    /// items, then re-runs the listing flow there with only those items - the normal
    /// same-retainer merge path does the delist, stack and relist.</summary>
    private bool StartNextFollowUp(string reason)
    {
        while (followUpQueue.Count > 0)
        {
            var (retainerId, retainerName, items) = followUpQueue.Dequeue();
            var index = FindRetainerSortedIndex(retainerId);
            if (index == null)
            {
                Svc.Chat.Print($"[AutoLister] Couldn't find {retainerName} at this summoning bell - merge those items manually.");
                continue;
            }

            followUpPhase = true;
            currentFollowUpItems = items;
            SuppressAutoRetainer(true);
            Svc.Chat.Print($"[AutoLister] Done here ({reason}). Swapping to {retainerName} to stack {items.Count} item(s)...");

            var sortedIndex = index.Value;
            taskManager.Enqueue(CloseSellList, "CloseSellList", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.EnqueueDelay(300);
            taskManager.Enqueue(CloseRetainerMenu, "CloseRetainerMenu", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.Enqueue(WaitRetainerClosed, "WaitRetainerClosed", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.EnqueueDelay(500);
            taskManager.Enqueue(() => OpenRetainer(sortedIndex), "OpenRetainer", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.Enqueue(SelectSellItems, "SelectSellItems", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.Enqueue(WaitSellListForFollowUp, "WaitSellListForFollowUp", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
            taskManager.EnqueueDelay(400);
            taskManager.Enqueue(BuildFollowUpItems, "BuildFollowUpItems");
            taskManager.Enqueue(ProcessNextItem, "ProcessNextItem");
            return true;
        }

        return false;
    }

    private static unsafe uint? FindRetainerSortedIndex(ulong retainerId)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return null;

        for (var i = 0u; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer != null && retainer->RetainerId == retainerId)
                return i;
        }

        return null;
    }

    private unsafe bool? WaitSellListForFollowUp()
    {
        if (ClickTalkIfOpen())
            return false;

        return TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && IsAddonReady(addon);
    }

    private unsafe bool? BuildFollowUpItems()
    {
        pendingItems.Clear();
        foreach (var (itemId, hq) in currentFollowUpItems)
        {
            if (FindInBags(itemId, hq, out var container, out var slot))
                pendingItems.Enqueue((container, slot, itemId));
            else
                Svc.Chat.Print($"[AutoLister] {ItemNameOf(itemId)}: no longer in your bags, skipping its merge.");
        }

        currentFollowUpItems = [];
        return true;
    }

    #region Pinch & Cull

    /// <summary>Dagobert-style repricing of the current listings, with the listing
    /// flow's economics on top: healthy listings get the undercut price confirmed,
    /// listings under the thresholds are returned to the bags and vendored.</summary>
    private unsafe void StartPinch(bool allRetainersRun)
    {
        if (taskManager.IsBusy)
            return;

        ResetRunState();
        runMode = RunMode.Pinch;

        if (allRetainersRun)
        {
            if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
            {
                Svc.Chat.Print("[AutoLister] Open the summoning bell's retainer list first.");
                return;
            }

            var manager = RetainerManager.Instance();
            for (var i = 0u; i < manager->GetRetainerCount(); i++)
            {
                var retainer = manager->GetRetainerBySortedIndex(i);
                if (retainer != null && retainer->MarketItemCount > 0)
                    retainerQueue.Enqueue(i);
            }

            if (retainerQueue.Count == 0)
            {
                Svc.Chat.Print("[AutoLister] No retainer has market listings.");
                return;
            }

            allRetainersMode = true;
            SuppressAutoRetainer(true);
            Svc.Chat.Print($"[AutoLister] Pinch & Cull across {retainerQueue.Count} retainer(s)...");
            taskManager.Enqueue(NextRetainer, "NextRetainer");
            return;
        }

        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
        {
            Svc.Chat.Print("[AutoLister] Open a retainer's sell list (Markets window) first.");
            return;
        }

        BuildListingQueue();
        if (pendingListingIndexes.Count == 0)
        {
            Svc.Chat.Print("[AutoLister] This retainer has no listings.");
            return;
        }

        Svc.Chat.Print($"[AutoLister] Pinch & Cull over {pendingListingIndexes.Count} listing(s)...");
        taskManager.Enqueue(ProcessNextListing, "ProcessNextListing");
    }

    /// <summary>Queues the sell list's UI row indexes, read from the addon's list
    /// component the way Dagobert does - the retainer market container's slot numbers
    /// do NOT correspond to the visible rows. Highest row first: culls remove rows,
    /// so working backwards keeps the remaining indexes valid.</summary>
    private unsafe void BuildListingQueue()
    {
        pendingListingIndexes.Clear();
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon))
            return;

        var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
        var list = listNode != null ? (AtkComponentList*)listNode->Component : null;
        if (list == null)
            return;

        for (var i = list->ListLength - 1; i >= 0; i--)
            pendingListingIndexes.Enqueue(i);
    }

    private unsafe bool? NextRetainer()
    {
        if (retainerQueue.Count == 0)
        {
            allRetainersMode = false;
            SuppressAutoRetainer(false);
            FinishRun("all retainers processed");
            return true;
        }

        var index = retainerQueue.Dequeue();
        taskManager.Enqueue(() => OpenRetainer(index), "OpenRetainer", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(SelectSellItems, "SelectSellItems", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(WaitSellListThenBuild, "WaitSellListThenBuild", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(ProcessNextListing, "ProcessNextListing");
        return true;
    }

    private unsafe bool? OpenRetainer(uint index)
    {
        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) && menu.IsAddonReady)
            return true; // Retainer menu already open.

        if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !IsAddonReady(addon))
            return false;

        if (EzThrottler.Throttle("ALPinch.OpenRetainer", 3000))
            Callback.Fire(addon, true, 2, (int)index);
        return false;
    }

    private bool? SelectSellItems()
    {
        if (ClickTalkIfOpen())
            return false;

        if (!TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) || !menu.IsAddonReady)
            return false;

        if (!EzThrottler.Throttle("ALPinch.SellItems", 2000))
            return false;

        // "Sell items in your inventory on the market" - match by text, with
        // Dagobert's hardcoded index 2 as the fallback.
        var entries = menu.Entries;
        var index = -1;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Text.Contains("sell", StringComparison.OrdinalIgnoreCase)
                && entries[i].Text.Contains("market", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0 && entries.Length > 2)
            index = 2;
        if (index < 0)
            return false;

        entries[index].Select();
        return true;
    }

    private unsafe bool? WaitSellListThenBuild()
    {
        if (ClickTalkIfOpen())
            return false;

        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon))
            return false;

        BuildListingQueue();
        return true;
    }

    private static bool ClickTalkIfOpen()
    {
        if (TryGetAddonMaster<AddonMaster.Talk>("Talk", out var talk) && talk.IsAddonReady)
        {
            if (EzThrottler.Throttle("ALPinch.Talk", 300))
                talk.Click();
            return true;
        }

        return false;
    }

    private unsafe bool? ProcessNextListing()
    {
        // A cull whose return step never completed (silent timeout, dialog mismatch)
        // leaves the flag set - surface it, because the item is in fact still listed
        // at its old price rather than back in the bags.
        if (cullCurrentItem)
        {
            cullCurrentItem = false;
            crashKeepItem = false;
            var unreturned = currentItemName.Length > 0 ? StripSpecial(currentItemName) : ItemNameOf(currentItemId);
            crashedReport.Add((unreturned, "could NOT be pulled - still listed at its old price"));
            Svc.Chat.Print($"[AutoLister] {unreturned}: could not be pulled back - it is still listed at its old price.");
        }

        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
        {
            Svc.Chat.Print("[AutoLister] Sell list closed, stopping.");
            SuppressAutoRetainer(false);
            PrintReport();
            return null;
        }

        if (pendingListingIndexes.Count == 0)
        {
            if (allRetainersMode)
            {
                taskManager.Enqueue(CloseSellList, "CloseSellList", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
                taskManager.EnqueueDelay(300);
                taskManager.Enqueue(CloseRetainerMenu, "CloseRetainerMenu", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
                taskManager.Enqueue(WaitRetainerClosed, "WaitRetainerClosed", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
                taskManager.EnqueueDelay(500);
                taskManager.Enqueue(NextRetainer, "NextRetainer");
            }
            else
            {
                FinishRun("all listings processed");
            }

            return true;
        }

        currentListingIndex = pendingListingIndexes.Dequeue();
        skipCurrentItem = false;
        cullCurrentItem = false;
        crashKeepItem = false;
        vendorCurrentItem = false;
        newPrice = null;
        compareOpenAt = 0;
        currentItemId = 0;
        currentItemName = "";
        crashDetail = "";
        currentQuantity = 1;
        currentHadBagCopy = false;
        returnLandedAt = 0;

        var index = currentListingIndex;
        taskManager.Enqueue(() => OpenListingContextMenu(index), "OpenListingContextMenu");
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(ClickAdjustPrice, "ClickAdjustPrice", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(RequestPrice, "RequestPrice", new TaskManagerConfiguration { TimeLimitMS = MbOpenDelayMs + 5000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(MbKeepOpenMs);
        taskManager.Enqueue(DecideReprice, "DecideReprice", new TaskManagerConfiguration { TimeLimitMS = MarketBoardResultTimeoutMs + 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(200);
        taskManager.Enqueue(Cleanup, "Cleanup", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(ReturnIfCulled, "ReturnIfCulled", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(400);
        taskManager.Enqueue(VendorViaRetainer, "VendorViaRetainer", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(150);
        taskManager.Enqueue(ClickHaveRetainerSell, "ClickHaveRetainerSell", new TaskManagerConfiguration { TimeLimitMS = 3000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(WaitVendorComplete, "WaitVendorComplete", new TaskManagerConfiguration { TimeLimitMS = 4000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.EnqueueDelay(300);
        taskManager.Enqueue(ProcessNextListing, "ProcessNextListing");
        return true;
    }

    private unsafe bool? OpenListingContextMenu(int index)
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon))
            return false;

        Callback.Fire(addon, true, 0, index, 1);
        return true;
    }

    private unsafe bool? ClickAdjustPrice()
    {
        if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) || !IsAddonReady(addon))
            return false;

        var entries = new ReaderContextMenu(addon).Entries;
        var index = entries.FindIndex(e =>
            e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));

        if (index < 0)
        {
            // Mannequin item or unexpected menu - leave it alone.
            Svc.Log.Debug($"No 'Adjust Price' entry ({string.Join(", ", entries.Select(e => e.Name))}), skipping listing");
            skipCurrentItem = true;
            skippedCount++;
            addon->Close(true);
            return true;
        }

        Callback.Fire(addon, true, 0, index, 0, 0, 0);
        return true;
    }

    /// <summary>The pinch counterpart of SetPriceAndConfirm: reprice when the listing
    /// stays above the thresholds, otherwise cancel and flag the item for the cull.</summary>
    private unsafe bool? DecideReprice()
    {
        if (skipCurrentItem)
            return true;

        if (newPrice == null)
            return false;

        if (TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchResult))
            searchResult->Close(true);

        if (!TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var itemName = GetRetainerSellItemName(addon);
        if (newPrice.Value <= 0)
        {
            Svc.Chat.Print($"[AutoLister] {itemName}: no market listings found, keeping the current price.");
            skippedCount++;
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        cachedPrices.TryAdd(itemName, newPrice.Value);

        // The listing's identity comes from the price window itself: quantity from
        // its numeric input, the item id by resolving the displayed name.
        currentQuantity = Math.Max(addon->Quantity->Value, 1);
        currentItemId = ResolveItemIdByName(itemName);
        currentItemName = itemName;
        currentItemHq = itemIsHq;
        currentHadBagCopy = currentItemId != 0 && BagsContain(currentItemId);

        // Price-crash guard: a big listing that would have to drop hard to undercut
        // gets pulled and kept instead of chasing the crash down (or vendoring it).
        var oldPrice = (long)addon->AskingPrice->Value;
        if (oldPrice > PriceCrashFloorGil && newPrice.Value * 100 <= oldPrice * (100 - PriceCrashDropPercent))
        {
            var dropPercent = 100 - newPrice.Value * 100 / oldPrice;
            cullCurrentItem = true;
            crashKeepItem = true;
            marketCountBeforeReturn = CurrentMarketItemCount();
            crashDetail = $"price crashed ({dropPercent}% decrease)";
            Svc.Chat.Print($"[AutoLister] {itemName}: PRICE CRASH - listed at {oldPrice:N0}, market now {newPrice.Value:N0} "
                + $"({dropPercent}% decrease). Pulling it back to your bags instead of repricing.");
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        var total = (long)newPrice.Value * currentQuantity;
        var vendorTotal = GetVendorPrice(currentItemId, itemIsHq) * currentQuantity;
        var marketNet = total * (100 - MarketTaxPercent) / 100;

        // An unresolvable item can't be safely vendored, so it only ever gets repriced.
        if (currentItemId != 0 && (total < VendorThresholdGil || vendorTotal > marketNet))
        {
            cullCurrentItem = true;
            marketCountBeforeReturn = CurrentMarketItemCount();
            Svc.Chat.Print(vendorTotal > marketNet
                ? $"[AutoLister] {itemName}: vendor pays {vendorTotal:N0} gil vs ~{marketNet:N0} from the market after tax - delisting to vendor."
                : $"[AutoLister] {itemName}: repricing would only total {total:N0} gil - delisting to vendor.");
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        addon->AskingPrice->SetValue(newPrice.Value);
        var change = DescribeChange(oldPrice, newPrice.Value);
        Svc.Chat.Print($"[AutoLister] {itemName}: repriced to {newPrice.Value:N0} gil ({change}).");
        repricedCount++;
        listedReport.Add((itemName, newPrice.Value, $"({change})"));
        Callback.Fire(&addon->AtkUnitBase, true, RetainerSellConfirmEvent);
        addon->AtkUnitBase.Close(true);
        return true;
    }

    /// <summary>Returns a culled listing (which the game puts into the RETAINER's
    /// inventory, not the player's bags), then either retrieves it to the bags (price
    /// crash) or aims the vendor tasks at the retainer slot it landed in.</summary>
    private unsafe bool? ReturnIfCulled()
    {
        if (skipCurrentItem || !cullCurrentItem)
            return true;

        if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
        {
            // The confirmation names the item; a mismatch means the wrong row was
            // targeted - answer no and let the safety report it still listed.
            var expected = StripSpecial(currentItemName);
            var dialog = StripSpecial(yesno.Text ?? "");
            if (expected.Length > 0 && dialog.Length > 0 && !dialog.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                Svc.Log.Warning($"Return dialog mismatch (\"{dialog}\" vs \"{expected}\") - answering no");
                yesno.No();
                return false;
            }

            if (EzThrottler.Throttle("ALPinch.Yes", 500))
                yesno.Yes();
            return false;
        }

        var stillListed = CurrentMarketItemCount() >= marketCountBeforeReturn;

        if (!stillListed)
        {
            // The market count drops before the item lands in the bags; give the
            // inventory a moment so the bag lookups below see it.
            if (returnLandedAt == 0)
            {
                returnLandedAt = Environment.TickCount64;
                return false;
            }

            if (Environment.TickCount64 - returnLandedAt < 600)
                return false;

            cullCurrentItem = false;
            var name = currentItemName.Length > 0 ? StripSpecial(currentItemName) : ItemNameOf(currentItemId);

            if (crashKeepItem)
            {
                crashKeepItem = false;
                var location = currentItemId != 0 && BagsContain(currentItemId) ? "pulled to your bags" : "check the retainer for it";
                crashedReport.Add((name, $"{crashDetail} - {location}"));
                Svc.Chat.Print($"[AutoLister] {name}: {location}.");
                return true;
            }

            if (currentHadBagCopy)
            {
                // The pulled stack merged into a stack that was already in the bags;
                // vendoring that slot would sell the pre-existing items too.
                returnedReport.Add(name);
                Svc.Chat.Print($"[AutoLister] {name}: pulled to your bags (you already carried some - vendor it manually).");
                return true;
            }

            if (currentItemId != 0 && FindInBags(currentItemId, out var bagContainer, out var bagSlot))
            {
                currentContainer = bagContainer;
                currentSlot = bagSlot;
                vendorCurrentItem = true;
                culledReport.Add(name);
                return true;
            }

            returnedReport.Add(name);
            Svc.Chat.Print($"[AutoLister] {name}: pulled back, but couldn't find it in your bags - vendor it manually.");
            return true;
        }

        // Still listed - drive the context menu's return-to-inventory entry.
        if (TryGetAddonByName<AtkUnitBase>("ContextMenu", out var menu) && IsAddonReady(menu))
        {
            var entries = new ReaderContextMenu(menu).Entries;
            var index = entries.FindIndex(e => IsReturnToInventoryEntry(e.Name));
            if (index < 0)
            {
                Svc.Log.Debug($"No return-to-inventory entry ({string.Join(", ", entries.Select(e => e.Name))})");
                skipCurrentItem = true;
                skippedCount++;
                cullCurrentItem = false;
                menu->Close(true);
                return true;
            }

            if (EzThrottler.Throttle("ALPinch.Return", 1000))
                Callback.Fire(menu, true, 0, index, 0, 0, 0);
            return false;
        }

        if (TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellList) && IsAddonReady(sellList)
            && EzThrottler.Throttle("ALPinch.ReopenMenu", 1500))
            Callback.Fire(sellList, true, 0, currentListingIndex, 1);
        return false;
    }

    /// <summary>"3% decrease" / "12% increase"; tiny moves show as "&lt;1%".</summary>
    private static string DescribeChange(long oldPrice, long newPrice)
    {
        var delta = newPrice - oldPrice;
        if (oldPrice <= 0 || delta == 0)
            return "no change";

        var percent = Math.Abs(delta) * 100.0 / oldPrice;
        var percentText = percent < 1 ? "<1" : $"{percent:0}";
        return $"{percentText}% {(delta < 0 ? "decrease" : "increase")}";
    }

    private static unsafe int CurrentMarketItemCount()
    {
        var retainer = RetainerManager.Instance()->GetActiveRetainer();
        return retainer != null ? retainer->MarketItemCount : 0;
    }

    /// <summary>Resolves an item id from the sell window's displayed name (HQ glyph and
    /// other special characters stripped). Returns 0 when nothing matches.</summary>
    private static uint ResolveItemIdByName(string name)
    {
        if (itemIdsByName == null)
        {
            itemIdsByName = [];
            foreach (var item in Svc.Data.GetExcelSheet<Item>())
            {
                var itemName = item.Name.ExtractText();
                if (itemName.Length > 0)
                    itemIdsByName.TryAdd(itemName.ToLowerInvariant(), item.RowId);
            }
        }

        var key = new string(name.Where(c => c < 0xE000 || c > 0xF8FF).ToArray()).Trim().ToLowerInvariant();
        return itemIdsByName.TryGetValue(key, out var id) ? id : 0;
    }

    private static unsafe bool BagsContain(uint itemId)
        => FindInBags(itemId, out _, out _);

    private static unsafe bool FindInBags(uint itemId, out InventoryType container, out int slot)
        => FindItemIn([InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4],
            itemId, null, out container, out slot);

    private static unsafe bool FindInBags(uint itemId, bool hq, out InventoryType container, out int slot)
        => FindItemIn([InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4],
            itemId, hq, out container, out slot);

    private static unsafe bool FindItemIn(InventoryType[] containers, uint itemId, bool? hq, out InventoryType container, out int slot)
    {
        var manager = InventoryManager.Instance();
        foreach (var candidate in containers)
        {
            var containerPtr = manager->GetInventoryContainer(candidate);
            if (containerPtr == null)
                continue;

            for (var i = 0; i < containerPtr->Size; i++)
            {
                var item = containerPtr->GetInventorySlot(i);
                if (item != null && item->ItemId == itemId
                    && (hq == null || item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == hq))
                {
                    container = candidate;
                    slot = i;
                    return true;
                }
            }
        }

        container = default;
        slot = -1;
        return false;
    }

    /// <summary>Strips SeString private-use glyphs (the HQ marker and friends).</summary>
    private static string StripSpecial(string text)
        => new(text.Where(c => c < 0xE000 || c > 0xF8FF).ToArray());

    /// <summary>Matches the "Return Items to Inventory" context entry - the one that
    /// pulls a listing straight back to the player's bags. The menu also has "Return
    /// to Retainer", which must never be picked (things get lost in the retainer).</summary>
    private static bool IsReturnToInventoryEntry(string name)
        => (name.Contains("return", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("zurück", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("récupér", StringComparison.CurrentCultureIgnoreCase)
                || name.Contains("返却", StringComparison.CurrentCultureIgnoreCase))
            && !name.Contains("retainer", StringComparison.CurrentCultureIgnoreCase)
            && !name.Contains("gehilfe", StringComparison.CurrentCultureIgnoreCase)
            && !name.Contains("servant", StringComparison.CurrentCultureIgnoreCase)
            && !name.Contains("リテイナー", StringComparison.CurrentCultureIgnoreCase);

    private static string ItemNameOf(uint itemId)
        => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item {itemId}";

    private static unsafe bool? CloseSellList()
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon))
            return true;
        addon->Close(true);
        return true;
    }

    /// <summary>Waits until the previous retainer's session has fully ended and the
    /// bell's retainer list is back. Without this, opening the next retainer can race
    /// the old retainer's still-open menu and re-enter the SAME retainer.</summary>
    private static unsafe bool? WaitRetainerClosed()
    {
        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) && menu.IsAddonReady)
            return false; // still inside a retainer's menu

        var manager = RetainerManager.Instance();
        if (manager != null && manager->GetActiveRetainer() != null)
            return false;

        return TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && IsAddonReady(list);
    }

    private static bool? CloseRetainerMenu()
    {
        if (ClickTalkIfOpen())
            return false;

        if (!TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) || !menu.IsAddonReady)
            return true;

        unsafe
        {
            ((AtkUnitBase*)menu.Base)->Close(true);
        }

        return true;
    }

    #endregion

    /// <summary>Chat report of everything listed (name left, price right) with items that
    /// need manual pricing at the end. Chat uses a proportional font, so exact column
    /// alignment isn't possible; dot leaders sized off the longest name come closest.</summary>
    private void PrintReport()
    {
        if (listedReport.Count == 0 && manualPricingReport.Count == 0 && culledReport.Count == 0
            && returnedReport.Count == 0 && crashedReport.Count == 0 && setAsideReport.Count == 0)
            return;

        var maxLen = listedReport.Select(e => e.Name.Length)
            .Concat(manualPricingReport.Select(n => n.Length))
            .Concat(culledReport.Select(n => n.Length))
            .Concat(returnedReport.Select(n => n.Length))
            .Concat(crashedReport.Select(e => e.Name.Length))
            .Concat(setAsideReport.Select(e => e.Name.Length))
            .Max();

        Svc.Chat.Print("[AutoLister] --- Listing report ---");
        foreach (var (name, price, change) in listedReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} {price,10:N0}g{(change.Length > 0 ? " " + change : "")}");
        foreach (var (name, detail) in crashedReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} {detail}");
        foreach (var name in culledReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} delisted & vendored");
        foreach (var name in returnedReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} delisted - find and vendor it manually");
        foreach (var (name, retainerName) in setAsideReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} set aside - already listed by {retainerName}");
        foreach (var name in manualPricingReport)
            Svc.Chat.Print($"{name} {Leaders(name, maxLen)} pending manual listing");

        static string Leaders(string name, int maxLen) => new('.', maxLen - name.Length + 3);
    }

    private unsafe bool? OpenItemContextMenu(InventoryType container, int slot)
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return null;

        // The owning addon id makes the context menu behave exactly like a real
        // right click on that inventory window.
        uint ownerId = 0;
        foreach (var name in (string[])["InventoryExpansion", "InventoryLarge", "Inventory"])
        {
            if (TryGetAddonByName<AtkUnitBase>(name, out var inventoryAddon) && inventoryAddon->IsVisible)
            {
                ownerId = inventoryAddon->Id;
                break;
            }
        }

        agent->OpenForItemSlot(container, slot, 0, ownerId);
        return true;
    }

    private unsafe bool? ClickPutUpForSale()
    {
        if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) || !IsAddonReady(addon))
            return false;

        var entries = new ReaderContextMenu(addon).Entries;
        var index = entries.FindIndex(e =>
            e.Name.Equals("put up for sale", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("mettre en vente", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("auf den märkten anbieten", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("出品する", StringComparison.CurrentCultureIgnoreCase));

        if (index < 0)
        {
            Svc.Log.Debug($"No 'Put Up for Sale' entry ({string.Join(", ", entries.Select(e => e.Name))}), skipping item");
            skipCurrentItem = true;
            skippedCount++;
            addon->Close(true);
            return true;
        }

        Callback.Fire(addon, true, 0, index, 0, 0, 0);
        return true;
    }

    /// <summary>Waits for the RetainerSell window, then either reuses a cached price or opens
    /// the price comparison so the market board data arrives via OfferingsReceived.</summary>
    private unsafe bool? RequestPrice()
    {
        if (skipCurrentItem)
            return true;

        if (!TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var itemName = GetRetainerSellItemName(addon);
        if (cachedPrices.TryGetValue(itemName, out var cached) && cached > 0)
        {
            Svc.Log.Debug($"{itemName}: using cached price {cached}");
            newPrice = cached;
            return true;
        }

        // Dagobert waits GetMBPricesDelayMS between the sell window appearing and the
        // Compare Prices click; without that pause the results sometimes never populate.
        var now = Environment.TickCount64;
        if (compareOpenAt == 0)
        {
            Svc.Log.Debug($"{itemName}: delaying market board open by {MbOpenDelayMs}ms");
            compareOpenAt = now + MbOpenDelayMs;
            return false;
        }

        if (now < compareOpenAt)
            return false;

        Svc.Log.Debug($"{itemName}: opening price comparison");
        Callback.Fire(&addon->AtkUnitBase, true, RetainerSellComparePricesEvent);
        return true;
    }

    private unsafe bool? SetPriceAndConfirm()
    {
        if (skipCurrentItem)
            return true;

        // Wait until the offerings handler (or one of its timeouts) produced a result.
        if (newPrice == null)
            return false;

        if (TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchResult))
            searchResult->Close(true);

        if (!TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var itemName = GetRetainerSellItemName(addon);
        if (newPrice.Value <= 0)
        {
            Svc.Chat.Print($"[AutoLister] {itemName}: no market listings found, skipping.");
            skippedCount++;
            manualPricingReport.Add(itemName);
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        cachedPrices.TryAdd(itemName, newPrice.Value);

        var quantity = GetCurrentItemQuantity();
        var total = (long)newPrice.Value * quantity;
        var vendorTotal = GetVendorPrice(currentItemId, itemIsHq) * quantity;
        var marketNet = total * (100 - MarketTaxPercent) / 100;

        if (total < VendorThresholdGil || vendorTotal > marketNet)
        {
            // Not worth a market slot - either too cheap outright, or the vendor pays
            // more than the listing would net after tax. Cancel the listing and let
            // the vendor tasks queued after Cleanup sell it through the retainer.
            vendorCurrentItem = true;
            Svc.Chat.Print(vendorTotal > marketNet
                ? $"[AutoLister] {itemName}: vendor pays {vendorTotal:N0} gil vs ~{marketNet:N0} from the market after tax, having the retainer sell it instead."
                : $"[AutoLister] {itemName}: listing would only total {total:N0} gil, having the retainer sell it instead.");
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        addon->AskingPrice->SetValue(newPrice.Value);
        Svc.Chat.Print($"[AutoLister] {itemName}: listed at {newPrice.Value:N0} gil{(mergeCompleted ? " (merged with the previous listing)" : "")}.");
        listedCount++;
        listedReport.Add((itemName, newPrice.Value, mergeCompleted ? "(merged stacks)" : ""));
        Callback.Fire(&addon->AtkUnitBase, true, RetainerSellConfirmEvent);
        addon->AtkUnitBase.Close(true);
        return true;
    }

    /// <summary>Per-unit price a vendor pays for the item; 0 when it can't be vendored.
    /// Vendors pay 10% extra for HQ.</summary>
    private static long GetVendorPrice(uint itemId, bool isHq)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return 0;

        var price = (long)item.Value.PriceLow;
        return isHq ? price * 11 / 10 : price;
    }

    private unsafe int GetCurrentItemQuantity()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(currentContainer);
        var slot = container != null ? container->GetInventorySlot(currentSlot) : null;
        return slot != null && slot->ItemId == currentItemId ? Math.Max((int)slot->Quantity, 1) : 1;
    }

    private unsafe bool? VendorViaRetainer()
    {
        if (skipCurrentItem || !vendorCurrentItem)
            return true;

        return OpenItemContextMenu(currentContainer, currentSlot);
    }

    private unsafe bool? ClickHaveRetainerSell()
    {
        if (skipCurrentItem || !vendorCurrentItem)
            return true;

        if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) || !IsAddonReady(addon))
            return false;

        var entries = new ReaderContextMenu(addon).Entries;
        var index = entries.FindIndex(e =>
            e.Name.Equals("have retainer sell items", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("vendre via le servant", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("gegenstand verkaufen lassen", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("リテイナーに売却させる", StringComparison.CurrentCultureIgnoreCase));

        if (index < 0)
        {
            Svc.Log.Debug($"No 'Have Retainer Sell Items' entry ({string.Join(", ", entries.Select(e => e.Name))}), skipping item");
            vendorCurrentItem = false;
            skippedCount++;
            addon->Close(true);
            return true;
        }

        Callback.Fire(addon, true, 0, index, 0, 0, 0);
        return true;
    }

    /// <summary>Vendoring is done once the item has left its bag slot; a confirmation
    /// dialog, if the game shows one, is answered with yes along the way.</summary>
    private unsafe bool? WaitVendorComplete()
    {
        if (skipCurrentItem || !vendorCurrentItem)
            return true;

        if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && IsAddonReady(yesno) && yesno->IsVisible)
        {
            new AddonMaster.SelectYesno(yesno).Yes();
            return false;
        }

        var container = InventoryManager.Instance()->GetInventoryContainer(currentContainer);
        var slot = container != null ? container->GetInventorySlot(currentSlot) : null;
        if (slot == null || slot->ItemId != currentItemId)
        {
            vendoredCount++;
            vendorCurrentItem = false;
            return true;
        }

        return false;
    }

    /// <summary>Closes any windows a timed-out step left behind so the next item starts clean.
    /// On the happy path everything is already closed and this is an immediate no-op.</summary>
    private unsafe bool? Cleanup()
    {
        newRequest = false;
        newPrice = null;

        if (TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchResult) && searchResult->IsVisible)
        {
            searchResult->Close(true);
            return false;
        }

        if (TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && retainerSell->AtkUnitBase.IsVisible)
        {
            Callback.Fire(&retainerSell->AtkUnitBase, true, RetainerSellCancelEvent);
            retainerSell->AtkUnitBase.Close(true);
            return false;
        }

        if (TryGetAddonByName<AtkUnitBase>("ContextMenu", out var contextMenu) && contextMenu->IsVisible)
        {
            contextMenu->Close(true);
            return false;
        }

        return true;
    }

    // The name node's raw text contains SeString payloads (colors, item link);
    // parse it and keep only the readable text (the HQ glyph survives as a char).
    private static unsafe string GetRetainerSellItemName(AddonRetainerSell* addon)
        => ReadSeString(&addon->ItemName->NodeText).TextValue;

    #endregion

    #region Market board price handling (mirrors Dagobert's MarketBoardHandler)

    private unsafe void OnRetainerSellListPostSetup(AddonEvent type, AddonArgs args)
        => RefreshActiveRetainerSnapshot();

    /// <summary>Records what the summoned retainer has on the market. Runs whenever a
    /// sell list opens (and when a run finishes), keeping the cross-retainer lookup
    /// fresh through normal play.</summary>
    private unsafe void RefreshActiveRetainerSnapshot()
    {
        var manager = RetainerManager.Instance();
        var retainer = manager != null ? manager->GetActiveRetainer() : null;
        if (retainer == null || retainer->RetainerId == 0)
            return;

        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null)
            return;

        var snapshot = new RetainerSnapshot { Name = retainer->NameString, At = DateTime.UtcNow };
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;
            if (item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                snapshot.HqItems.Add(item->ItemId);
            else
                snapshot.Items.Add(item->ItemId);
        }

        config.RetainerListings[retainer->RetainerId] = snapshot;
        config.Save();
    }

    /// <summary>Whether the summoned retainer has the item (matching HQ state) on the
    /// market and whether any of those listings has room for the bag stack under the
    /// per-listing cap. Read from the RetainerMarket container; which UI ROW a
    /// listing occupies can't be derived from it (the list is category-sorted), so
    /// the merge flow identifies rows by probing price windows instead.</summary>
    private static unsafe MergeAvail CheckActiveRetainerListings(uint itemId, bool hq, int bagQty, int maxPerListing)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null)
            return MergeAvail.None;

        var found = false;
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId != itemId
                || item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != hq)
                continue;

            found = true;
            if (item->Quantity + bagQty <= maxPerListing)
                return MergeAvail.Mergeable;
        }

        return found ? MergeAvail.Full : MergeAvail.None;
    }

    /// <summary>Row count of the sell list's list component (same node Dagobert reads).</summary>
    private static unsafe int SellListRowCount()
    {
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon))
            return 0;

        var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
        var list = listNode != null ? (AtkComponentList*)listNode->Component : null;
        return list != null ? list->ListLength : 0;
    }

    private unsafe bool TryFindOtherRetainerListing(uint itemId, bool hq, out ulong retainerId, out string retainerName)
    {
        retainerId = 0;
        retainerName = "";
        var manager = RetainerManager.Instance();
        var active = manager != null ? manager->GetActiveRetainer() : null;
        var activeId = active != null ? active->RetainerId : 0;

        foreach (var (id, snapshot) in config.RetainerListings)
        {
            if (id == activeId)
                continue;
            if ((hq ? snapshot.HqItems : snapshot.Items).Contains(itemId))
            {
                retainerId = id;
                retainerName = snapshot.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>State machine that finds the current item's listing row by probing:
    /// open a row's context menu, open Adjust Price, read the item off the price
    /// window (resolved id + HQ glyph), cancel; on a match, reopen that row's menu
    /// and click Return Items to Inventory. Row order can't be derived from the
    /// market container - the list is category-sorted - so probing is the only
    /// identification that can't pull the wrong item.</summary>
    private unsafe bool? MergeLocateAndPull()
    {
        if (!mergePending)
            return true;

        var now = Environment.TickCount64;
        if (now > mergeStepDeadline)
        {
            // The current step went nowhere (window never opened, menu ate the
            // click); retry from the next row rather than hanging the run.
            Svc.Log.Debug($"Merge probe step {mergeStep} timed out on row {mergeProbeRow}");
            if (mergeStep is MergeStep.ReopenMenu or MergeStep.ClickReturn or MergeStep.WaitReturned)
            {
                GiveUpMerge("the pull did not complete");
                return true;
            }

            mergeProbeRow++;
            SetMergeStep(MergeStep.OpenProbeMenu);
        }

        switch (mergeStep)
        {
            case MergeStep.OpenProbeMenu:
            case MergeStep.ReopenMenu:
                if (mergeStep == MergeStep.OpenProbeMenu && mergeProbeRow >= mergeRowCount)
                {
                    GiveUpMerge("couldn't find its listing row");
                    return true;
                }

                if (TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellList) && IsAddonReady(sellList)
                    && EzThrottler.Throttle("ALMerge.OpenMenu", 800))
                {
                    Callback.Fire(sellList, true, 0, mergeProbeRow, 1);
                    SetMergeStep(mergeStep == MergeStep.OpenProbeMenu ? MergeStep.ClickAdjust : MergeStep.ClickReturn);
                }

                return false;

            case MergeStep.ClickAdjust:
                if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var probeMenu) || !IsAddonReady(probeMenu))
                    return false;

                var probeEntries = new ReaderContextMenu(probeMenu).Entries;
                var adjustIndex = probeEntries.FindIndex(e =>
                    e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
                    || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
                    || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
                    || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));
                if (adjustIndex < 0)
                {
                    probeMenu->Close(true);
                    mergeProbeRow++;
                    SetMergeStep(MergeStep.OpenProbeMenu);
                    return false;
                }

                if (EzThrottler.Throttle("ALMerge.Adjust", 800))
                {
                    Callback.Fire(probeMenu, true, 0, adjustIndex, 0, 0, 0);
                    SetMergeStep(MergeStep.ReadWindow);
                }

                return false;

            case MergeStep.ReadWindow:
                if (!TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var priceWindow) || !IsAddonReady(&priceWindow->AtkUnitBase))
                    return false;

                // Item id + HQ must match AND this specific listing must have room
                // for the bag stack - a full 99 stack (or one that would overflow
                // the per-listing cap) is skipped so a roomier duplicate can match.
                var windowName = GetRetainerSellItemName(priceWindow);
                var windowQty = Math.Max(priceWindow->Quantity->Value, 1);
                var isMatch = ResolveItemIdByName(windowName) == currentItemId
                              && windowName.Contains(HqGlyph) == currentItemHq
                              && windowQty + mergeBagQuantity <= mergeMaxPerListing;
                Callback.Fire(&priceWindow->AtkUnitBase, true, RetainerSellCancelEvent);
                priceWindow->AtkUnitBase.Close(true);

                if (isMatch)
                {
                    SetMergeStep(MergeStep.ReopenMenu);
                }
                else
                {
                    mergeProbeRow++;
                    SetMergeStep(MergeStep.OpenProbeMenu);
                }

                return false;

            case MergeStep.ClickReturn:
                if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var returnMenu) || !IsAddonReady(returnMenu))
                    return false;

                var returnEntries = new ReaderContextMenu(returnMenu).Entries;
                var returnIndex = returnEntries.FindIndex(e => IsReturnToInventoryEntry(e.Name));
                if (returnIndex < 0)
                {
                    Svc.Log.Debug($"No return-to-inventory entry ({string.Join(", ", returnEntries.Select(e => e.Name))})");
                    returnMenu->Close(true);
                    GiveUpMerge("its context menu has no return-to-inventory entry");
                    return true;
                }

                if (EzThrottler.Throttle("ALMerge.Return", 800))
                {
                    Callback.Fire(returnMenu, true, 0, returnIndex, 0, 0, 0);
                    SetMergeStep(MergeStep.WaitReturned);
                }

                return false;

            case MergeStep.WaitReturned:
                if (TryGetAddonMaster<AddonMaster.SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
                {
                    var text = StripSpecial(yesno.Text ?? "");
                    if (text.Length > 0 && !text.Contains(mergeItemName, StringComparison.OrdinalIgnoreCase))
                    {
                        Svc.Log.Warning($"Merge return dialog mismatch (\"{text}\" vs \"{mergeItemName}\")");
                        yesno.No();
                        GiveUpMerge("the confirmation named a different item");
                        return true;
                    }

                    if (EzThrottler.Throttle("ALMerge.Yes", 500))
                        yesno.Yes();
                    return false;
                }

                if (CurrentMarketItemCount() < mergeMarketCountBefore)
                {
                    mergePending = false;
                    mergeCompleted = true;
                    return true;
                }

                return false;

            default:
                return true;
        }
    }

    private void SetMergeStep(MergeStep step)
    {
        mergeStep = step;
        mergeStepDeadline = Environment.TickCount64 + MergeStepTimeoutMs;
    }

    private void GiveUpMerge(string reason)
    {
        mergePending = false;
        Svc.Chat.Print($"[AutoLister] {mergeItemName}: {reason} - listing the bag stack separately.");
    }

    private unsafe void OnRetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
        var name = GetRetainerSellItemName((AddonRetainerSell*)args.Addon.Address);
        itemIsHq = name.Contains(HqGlyph);
    }

    private void OnItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
    {
        newRequest = true;
        useHq = itemIsHq;
        pendingNoMatchRequestId = -1;
        pendingNoMatchTimeoutAt = 0;
        pendingNoOfferingsTimeoutAt = Environment.TickCount64 + MarketBoardResultTimeoutMs;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (!newRequest || offerings.RequestId == lastRequestId)
            return;

        pendingNoOfferingsTimeoutAt = 0;

        if (offerings.ItemListings.Count == 0)
        {
            CompletePriceRequest(-1, offerings.RequestId);
            return;
        }

        // For an HQ item undercut the cheapest HQ listing; for NQ (or items that can't
        // be HQ) the cheapest listing overall. Listings arrive sorted by price.
        var wantHq = useHq
                     && (Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(offerings.ItemListings[0].ItemId)?.CanBeHq ?? false);

        var index = 0;
        while (wantHq && index < offerings.ItemListings.Count && !offerings.ItemListings[index].IsHq)
            index++;

        if (index >= offerings.ItemListings.Count)
        {
            if (offerings.ItemListings.Count < ListingsPerBatch)
            {
                // That was the last batch and it had no HQ listing.
                CompletePriceRequest(-1, offerings.RequestId);
                return;
            }

            // A full batch without a match - more batches may follow for this request.
            pendingNoMatchRequestId = offerings.RequestId;
            pendingNoMatchTimeoutAt = Environment.TickCount64 + MarketBoardResultTimeoutMs;
            return;
        }

        pendingNoMatchRequestId = -1;
        pendingNoMatchTimeoutAt = 0;
        var price = Math.Max((int)offerings.ItemListings[index].PricePerUnit - UndercutGil, 1);
        CompletePriceRequest(price, offerings.RequestId);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!newRequest)
            return;

        var now = Environment.TickCount64;
        if (pendingNoMatchRequestId >= 0 && now >= pendingNoMatchTimeoutAt)
        {
            Svc.Log.Debug("No matching market board listing received before timeout");
            CompletePriceRequest(-1, pendingNoMatchRequestId);
        }
        else if (pendingNoOfferingsTimeoutAt > 0 && now >= pendingNoOfferingsTimeoutAt)
        {
            Svc.Log.Debug("No market board offerings received before timeout");
            CompletePriceRequest(-1, -1);
        }
    }

    private void CompletePriceRequest(int price, int requestId)
    {
        pendingNoMatchRequestId = -1;
        pendingNoMatchTimeoutAt = 0;
        pendingNoOfferingsTimeoutAt = 0;
        if (requestId >= 0)
            lastRequestId = requestId;
        newRequest = false;
        newPrice = price;
    }

    #endregion
}
