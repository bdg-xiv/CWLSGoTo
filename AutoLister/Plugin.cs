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

    // Below this total (price x stack quantity) an item is vendored through the
    // retainer instead of being listed on the market.
    private const long VendorThresholdGil = 2000;
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
        PluginInterface.UiBuilder.Draw += DrawOverlay;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawOverlay;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnRetainerSellPostSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", OnItemSearchResultPostSetup);
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
        if (!TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !IsAddonReady(addon) || !addon->IsVisible)
            return;

        // Same anchor node Dagobert overlays its Auto Pinch button on; ours goes to its left.
        var node = addon->UldManager.NodeList[17];
        if (node == null)
            return;

        var pos = GetNodePosition(node);
        var scale = GetNodeScale(node);

        var label = taskManager.IsBusy ? "Cancel" : "Auto List";
        var buttonWidth = (ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + 12f) * scale.X;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(pos.X - buttonWidth - 4f * scale.X, pos.Y));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.Begin("###AutoListerButton",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);

        if (taskManager.IsBusy)
        {
            if (ImGui.Button("Cancel"))
                CancelListing();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancels the auto listing process");
        }
        else
        {
            if (ImGui.Button("Auto List"))
                StartListing();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Puts every sellable item from the bottom-right inventory quarter up for sale,\nundercutting the cheapest matching HQ/NQ listing by 1 gil.\nPlease do not interact with the game while this runs.");
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

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
        skipCurrentItem = false;
        newRequest = false;
        Svc.Chat.Print("[AutoLister] Cancelled.");
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
            return null; // Aborts the queue.
        }

        var retainer = RetainerManager.Instance()->GetActiveRetainer();
        if (retainer == null)
        {
            Svc.Chat.Print("[AutoLister] No active retainer, stopping.");
            return null;
        }

        if (retainer->MarketItemCount >= RetainerSellSlots)
        {
            FinishRun("the retainer's sell slots are full");
            return true;
        }

        // Pull the next pending item that is still sitting in its physical slot.
        var manager = InventoryManager.Instance();
        var found = false;
        InventoryType itemContainer = default;
        var slot = -1;
        var pendingItemId = 0u;
        while (pendingItems.Count > 0)
        {
            var candidate = pendingItems.Dequeue();
            var containerPtr = manager->GetInventoryContainer(candidate.Container);
            var inventorySlot = containerPtr != null ? containerPtr->GetInventorySlot(candidate.Slot) : null;
            if (inventorySlot != null && inventorySlot->ItemId == candidate.ItemId)
            {
                itemContainer = candidate.Container;
                slot = candidate.Slot;
                pendingItemId = candidate.ItemId;
                found = true;
                break;
            }
        }

        if (!found)
        {
            FinishRun("no sellable items left in the bottom-right quarter");
            return true;
        }

        skipCurrentItem = false;
        vendorCurrentItem = false;
        newPrice = null;
        compareOpenAt = 0;
        currentContainer = itemContainer;
        currentSlot = slot;
        currentItemId = pendingItemId;

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
        var summary = $"[AutoLister] Done ({reason}). Listed {listedCount} item(s).";
        if (vendoredCount > 0)
            summary += $" Vendored {vendoredCount}.";
        if (skippedCount > 0)
            summary += $" Skipped {skippedCount}.";
        Svc.Chat.Print(summary);
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
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        cachedPrices.TryAdd(itemName, newPrice.Value);

        var total = (long)newPrice.Value * GetCurrentItemQuantity();
        if (total < VendorThresholdGil)
        {
            // Too cheap to be worth a market slot - cancel the listing and let the
            // vendor tasks queued after Cleanup sell it through the retainer instead.
            vendorCurrentItem = true;
            Svc.Chat.Print($"[AutoLister] {itemName}: listing would only total {total:N0} gil, having the retainer sell it instead.");
            Callback.Fire(&addon->AtkUnitBase, true, RetainerSellCancelEvent);
            addon->AtkUnitBase.Close(true);
            return true;
        }

        addon->AskingPrice->SetValue(newPrice.Value);
        Svc.Chat.Print($"[AutoLister] {itemName}: listed at {newPrice.Value:N0} gil.");
        listedCount++;
        Callback.Fire(&addon->AtkUnitBase, true, RetainerSellConfirmEvent);
        addon->AtkUnitBase.Close(true);
        return true;
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

    private static unsafe string GetRetainerSellItemName(AddonRetainerSell* addon)
        => addon->ItemName->NodeText.ToString();

    #endregion

    #region Market board price handling (mirrors Dagobert's MarketBoardHandler)

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
