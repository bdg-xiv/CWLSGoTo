using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static ECommons.GenericHelpers;
using Callback = ECommons.Automation.Callback;

namespace ExcessEntrust;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "ExcessEntrust";
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string Command = "/excess";

    private readonly Configuration config;
    private readonly TaskManager taskManager;

    // ---- Run state ------------------------------------------------------------
    private sealed record RetainerJob(uint Index, string Name, EntrustPlanDef Plan);
    private sealed class EntrustPlanDef
    {
        public string Name = string.Empty;
        public bool Duplicates;
        public Dictionary<uint, int> CategoryKeeps = new(); // ItemUICategory -> keep
        public Dictionary<uint, int> ItemKeeps = new();     // itemId -> keep
        public HashSet<uint> Items = new();
        public bool HasCriteria => Duplicates || CategoryKeeps.Count > 0 || Items.Count > 0;
    }

    private enum MoveStep { Idle, WaitSplit, WaitMove }

    private static readonly InventoryType[] PlayerBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] RetainerPages =
        [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
         InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7];

    private readonly Queue<RetainerJob> retainerQueue = new();
    private Dictionary<uint, int> requirements = new();
    private RetainerJob currentJob;
    private List<(uint Id, int Left)> entrustList = new();
    private int entrustIndex;
    private MoveStep moveStep;
    private long stepDeadline;
    private uint moveItem;
    private int moveQty;
    private int splitWant;
    private InventoryType moveDstType;
    private int moveDstSlot;
    private int totalUnits;
    private int totalItems;
    private readonly List<string> notes = new();

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 30000, AbortOnTimeout = true });

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "With the retainer bell list open: entrust excess materials to each retainer per its AutoRetainer plan, " +
                          "keeping what the selected Artisan list needs. '/excess list' shows lists, '/excess list <name>' selects, " +
                          "'/excess none' clears, '/excess stop' aborts.",
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);
        SuppressAutoRetainer(false);
        ECommonsMain.Dispose();
    }

    private void OnCommand(string cmd, string args)
    {
        var arg = args.Trim();
        if (arg.Length == 0)
        {
            StartRun();
            return;
        }

        if (arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            taskManager.Abort();
            SuppressAutoRetainer(false);
            moveStep = MoveStep.Idle;
            Print("Stopped.");
            return;
        }

        if (arg.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            config.SelectedListId = 0;
            config.SelectedListName = string.Empty;
            PluginInterface.SavePluginConfig(config);
            Print("List selection cleared - only AutoRetainer plan keeps will apply.");
            return;
        }

        if (arg.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var lists = ReadArtisanLists();
            if (lists.Count == 0)
            {
                Print("No Artisan crafting lists found.");
                return;
            }
            Print("Artisan lists:");
            foreach (var l in lists)
            {
                var marker = l.Id == config.SelectedListId ? " <- selected" : string.Empty;
                Print($"  {l.Id}: {l.Name} ({l.Recipes.Count} recipe(s)){marker}");
            }
            return;
        }

        if (arg.StartsWith("list ", StringComparison.OrdinalIgnoreCase))
        {
            var query = arg[5..].Trim();
            var lists = ReadArtisanLists();
            var match = lists.FirstOrDefault(l => l.Id.ToString() == query)
                        ?? lists.FirstOrDefault(l => l.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                        ?? lists.FirstOrDefault(l => l.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                Print($"No Artisan list matches '{query}'.");
                return;
            }
            config.SelectedListId = match.Id;
            config.SelectedListName = match.Name;
            PluginInterface.SavePluginConfig(config);
            Print($"Selected list '{match.Name}' ({match.Id}) - its materials will be kept in your bags.");
            return;
        }

        Print("Usage: /excess | /excess list [name] | /excess none | /excess stop");
    }

    // ---- Artisan list reading -------------------------------------------------

    private sealed class ArtisanList
    {
        public int Id;
        public string Name = string.Empty;
        public List<(uint RecipeId, int Quantity)> Recipes = new();
    }

    private static string PluginConfigsDir => Svc.PluginInterface.ConfigFile.Directory!.FullName;

    private static List<ArtisanList> ReadArtisanLists()
    {
        var result = new List<ArtisanList>();
        try
        {
            var path = Path.Combine(PluginConfigsDir, "Artisan.json");
            if (!File.Exists(path)) return result;

            using var sr = new StreamReader(path);
            using var jr = new JsonTextReader(sr);
            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName || (string)jr.Value! != "NewCraftingLists" || jr.Depth > 1)
                    continue;
                jr.Read();
                var arr = JArray.Load(jr);
                foreach (var entry in arr.OfType<JObject>())
                {
                    var list = new ArtisanList
                    {
                        Id = entry.Value<int?>("ID") ?? 0,
                        Name = entry.Value<string>("Name") ?? string.Empty,
                    };
                    if (entry["Recipes"] is JArray recipes)
                        foreach (var r in recipes.OfType<JObject>())
                        {
                            var id = r.Value<uint?>("ID") ?? 0;
                            var qty = r.Value<int?>("Quantity") ?? 0;
                            if (id > 0 && qty > 0)
                                list.Recipes.Add((id, qty));
                        }
                    if (list.Id != 0)
                        result.Add(list);
                }
                break;
            }
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[ExcessEntrust] Failed to read Artisan lists");
        }
        return result;
    }

    /// <summary>
    /// Material requirements exactly like Artisan's Ingredients tab "Required" column:
    /// the direct ingredients of every list entry, summed. Artisan lists include
    /// precraft entries, so this covers raw materials without double counting.
    /// </summary>
    private static Dictionary<uint, int> ComputeRequirements(ArtisanList list)
    {
        var needed = new Dictionary<uint, int>();
        var sheet = Svc.Data.GetExcelSheet<Recipe>();
        foreach (var (recipeId, quantity) in list.Recipes)
        {
            var row = sheet.GetRowOrDefault(recipeId);
            if (row == null) continue;
            var recipe = row.Value;
            for (var i = 0; i < recipe.Ingredient.Count; i++)
            {
                var itemId = recipe.Ingredient[i].RowId;
                var amount = (int)recipe.AmountIngredient[i];
                if (itemId == 0 || amount == 0) continue;
                needed[itemId] = needed.GetValueOrDefault(itemId) + amount * quantity;
            }
        }
        return needed;
    }

    // ---- AutoRetainer plan reading --------------------------------------------

    private static Dictionary<string, EntrustPlanDef> ReadRetainerPlans()
    {
        var result = new Dictionary<string, EntrustPlanDef>();
        try
        {
            var path = Path.Combine(PluginConfigsDir, "AutoRetainer", "DefaultConfig.json");
            if (!File.Exists(path)) return result;

            var root = JObject.Parse(File.ReadAllText(path));
            var plans = new Dictionary<string, EntrustPlanDef>();
            foreach (var p in (root["EntrustPlans"] as JArray)?.OfType<JObject>() ?? [])
            {
                var def = new EntrustPlanDef
                {
                    Name = p.Value<string>("Name") ?? string.Empty,
                    Duplicates = p.Value<bool?>("Duplicates") ?? false,
                };
                foreach (var c in (p["EntrustCategories"] as JArray)?.OfType<JObject>() ?? [])
                {
                    var id = c.Value<uint?>("ID") ?? 0;
                    if (id > 0) def.CategoryKeeps[id] = c.Value<int?>("AmountToKeep") ?? 0;
                }
                foreach (var token in (p["EntrustItems"] as JArray) ?? [])
                    if (token.Type == JTokenType.Integer)
                        def.Items.Add(token.Value<uint>());
                if (p["EntrustItemsAmountToKeep"] is JObject keeps)
                    foreach (var kv in keeps.Properties())
                        if (uint.TryParse(kv.Name, out var itemId))
                            def.ItemKeeps[itemId] = kv.Value.Value<int>();
                var guid = p.Value<string>("Guid");
                if (guid != null) plans[guid] = def;
            }

            var prefix = $"#{ECommons.GameHelpers.Player.CID:X16} ";
            if (root["AdditionalData"] is JObject additional)
                foreach (var kv in additional.Properties())
                {
                    if (!kv.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var retainerName = kv.Name[prefix.Length..];
                    var guid = (kv.Value as JObject)?.Value<string>("EntrustPlan");
                    if (guid != null && plans.TryGetValue(guid, out var def) && def.HasCriteria)
                        result[retainerName] = def;
                }
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[ExcessEntrust] Failed to read AutoRetainer entrust plans");
        }
        return result;
    }

    // ---- Run ------------------------------------------------------------------

    private unsafe void StartRun()
    {
        if (taskManager.IsBusy)
        {
            Print("Already running - '/excess stop' to abort.");
            return;
        }

        if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
        {
            Print("Open the summoning bell's retainer list first.");
            return;
        }

        requirements = new Dictionary<uint, int>();
        if (config.SelectedListId != 0)
        {
            var list = ReadArtisanLists().FirstOrDefault(l => l.Id == config.SelectedListId);
            if (list == null)
            {
                Print($"Selected Artisan list '{config.SelectedListName}' no longer exists - '/excess list' to pick another, or '/excess none'.");
                return;
            }
            requirements = ComputeRequirements(list);
            Print($"Keeping materials for list '{list.Name}' ({requirements.Count} distinct ingredients).");
        }

        var plans = ReadRetainerPlans();
        if (plans.Count == 0)
        {
            Print("No AutoRetainer entrust plans found for this character.");
            return;
        }

        retainerQueue.Clear();
        notes.Clear();
        totalUnits = 0;
        totalItems = 0;
        var manager = RetainerManager.Instance();
        for (var i = 0u; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0) continue;
            var name = retainer->NameString;
            if (plans.TryGetValue(name, out var plan))
                retainerQueue.Enqueue(new RetainerJob(i, name, plan));
        }

        if (retainerQueue.Count == 0)
        {
            Print("None of the listed retainers has an entrust plan assigned in AutoRetainer.");
            return;
        }

        SuppressAutoRetainer(true);
        Print($"Entrusting excess across {retainerQueue.Count} retainer(s)...");
        taskManager.Enqueue(NextRetainer, "NextRetainer");
    }

    private bool? NextRetainer()
    {
        if (retainerQueue.Count == 0)
        {
            Finish();
            return true;
        }

        currentJob = retainerQueue.Dequeue();
        taskManager.Enqueue(() => OpenRetainer(currentJob.Index), "OpenRetainer", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(SelectEntrustItems, "SelectEntrustItems", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(WaitEntrustLoaded, "WaitEntrustLoaded", new TaskManagerConfiguration { TimeLimitMS = 15000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(BuildEntrustList, "BuildEntrustList");
        taskManager.Enqueue(EntrustTick, "EntrustTick", new TaskManagerConfiguration { TimeLimitMS = 300000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(CloseTransferWindow, "CloseTransferWindow", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(CloseRetainerMenu, "CloseRetainerMenu", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(WaitRetainerClosed, "WaitRetainerClosed", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false, TimeoutSilently = true });
        taskManager.Enqueue(NextRetainer, "NextRetainer");
        return true;
    }

    private void Finish()
    {
        SuppressAutoRetainer(false);
        Print($"Done - entrusted {totalUnits} unit(s) across {totalItems} stack move(s).");
        foreach (var note in notes)
            Print($"  {note}");
    }

    // ---- Retainer navigation (AutoLister-proven) ------------------------------

    private static bool ClickTalkIfOpen()
    {
        if (TryGetAddonMaster<AddonMaster.Talk>("Talk", out var talk) && talk.IsAddonReady)
        {
            if (EzThrottler.Throttle("EE.Talk", 300))
                talk.Click();
            return true;
        }
        return false;
    }

    private unsafe bool? OpenRetainer(uint index)
    {
        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) && menu.IsAddonReady)
            return true;

        if (!TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !IsAddonReady(addon))
            return false;

        if (EzThrottler.Throttle("EE.OpenRetainer", 3000))
            Callback.Fire(addon, true, 2, (int)index);
        return false;
    }

    private static string entrustEntryText;

    private bool? SelectEntrustItems()
    {
        if (ClickTalkIfOpen())
            return false;

        if (!TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) || !menu.IsAddonReady)
            return false;

        if (!EzThrottler.Throttle("EE.SelectEntrust", 2000))
            return false;

        // "Entrust or withdraw items." - Addon sheet row 2378 (locale-proof), with a
        // keyword fallback.
        entrustEntryText ??= Svc.Data.GetExcelSheet<Addon>().GetRowOrDefault(2378)?.Text.ExtractText() ?? "Entrust";
        var entries = menu.Entries;
        foreach (var entry in entries)
        {
            if (entry.Text.Contains(entrustEntryText, StringComparison.OrdinalIgnoreCase)
                || entry.Text.Contains("entrust", StringComparison.OrdinalIgnoreCase))
            {
                entry.Select();
                return true;
            }
        }
        return false;
    }

    private static unsafe bool? WaitEntrustLoaded()
    {
        if (ClickTalkIfOpen())
            return false;

        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerPage1);
        return container != null && container->IsLoaded;
    }

    private static unsafe bool? CloseTransferWindow()
    {
        foreach (var name in (string[])["InventoryRetainerLarge", "InventoryRetainer"])
        {
            if (TryGetAddonByName<AtkUnitBase>(name, out var addon) && IsAddonReady(addon))
            {
                addon->Close(true);
                return true;
            }
        }
        return true;
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

    private static unsafe bool? WaitRetainerClosed()
    {
        if (ClickTalkIfOpen())
            return false;

        if (TryGetAddonMaster<AddonMaster.SelectString>("SelectString", out var menu) && menu.IsAddonReady)
            return false;

        var manager = RetainerManager.Instance();
        if (manager != null && manager->GetActiveRetainer() != null)
            return false;

        return TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && IsAddonReady(list);
    }

    // ---- Entrust engine -------------------------------------------------------

    private unsafe bool? BuildEntrustList()
    {
        entrustList = new List<(uint, int)>();
        entrustIndex = 0;
        moveStep = MoveStep.Idle;

        var manager = InventoryManager.Instance();
        var items = Svc.Data.GetExcelSheet<Item>();
        var plan = currentJob.Plan;

        // Total per item across the bags (collectables excluded).
        var counts = new Dictionary<uint, int>();
        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                if (slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)) continue;
                counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }

        foreach (var (id, total) in counts)
        {
            var row = items.GetRowOrDefault(id);
            if (row == null || row.Value.StackSize <= 1 || row.Value.IsUnique) continue;

            int keep;
            if (plan.Items.Contains(id) || plan.ItemKeeps.ContainsKey(id))
                keep = plan.ItemKeeps.GetValueOrDefault(id);
            else if (plan.CategoryKeeps.TryGetValue(row.Value.ItemUICategory.RowId, out var categoryKeep))
                keep = categoryKeep;
            else if (plan.Duplicates && RetainerHasItem(id))
                keep = 0;
            else
                continue;

            keep = Math.Max(keep, requirements.GetValueOrDefault(id));
            var toEntrust = total - keep;
            if (toEntrust > 0)
                entrustList.Add((id, toEntrust));
        }

        Print($"{currentJob.Name} ({currentJob.Plan.Name}): {entrustList.Count} item type(s) with excess.");
        return true;
    }

    private static unsafe bool RetainerHasItem(uint itemId)
    {
        var manager = InventoryManager.Instance();
        foreach (var page in RetainerPages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    return true;
            }
        }
        return false;
    }

    private unsafe bool? EntrustTick()
    {
        var now = Environment.TickCount64;
        var manager = InventoryManager.Instance();

        if (moveStep == MoveStep.WaitSplit)
        {
            if (FindBagStack(moveItem, splitWant, out _, out _))
            {
                moveStep = MoveStep.Idle;
            }
            else if (now > stepDeadline)
            {
                notes.Add($"{ItemNameOf(moveItem)}: split failed (bags full?) - skipped on {currentJob.Name}");
                entrustIndex++;
                moveStep = MoveStep.Idle;
            }
            return false;
        }

        if (moveStep == MoveStep.WaitMove)
        {
            var dst = manager->GetInventorySlot(moveDstType, moveDstSlot);
            if (dst != null && dst->ItemId == moveItem)
            {
                totalUnits += moveQty;
                totalItems++;
                var (id, left) = entrustList[entrustIndex];
                entrustList[entrustIndex] = (id, left - moveQty);
                if (left - moveQty <= 0) entrustIndex++;
                moveStep = MoveStep.Idle;
            }
            else if (now > stepDeadline)
            {
                notes.Add($"{ItemNameOf(moveItem)}: move did not complete - skipped on {currentJob.Name}");
                entrustIndex++;
                moveStep = MoveStep.Idle;
            }
            return false;
        }

        if (entrustIndex >= entrustList.Count)
            return true;

        var (itemId, remaining) = entrustList[entrustIndex];
        if (remaining <= 0)
        {
            entrustIndex++;
            return false;
        }

        if (!PickSourceStack(itemId, remaining, out var srcType, out var srcSlot, out var srcQty))
        {
            entrustIndex++;
            return false;
        }

        if (srcQty <= remaining)
        {
            if (!FindEmptyRetainerSlot(out var dstType, out var dstSlot))
            {
                notes.Add($"{currentJob.Name}: retainer inventory full, remaining excess skipped");
                entrustIndex = entrustList.Count;
                return false;
            }
            if (!EzThrottler.Throttle("EE.Op", 350)) return false;
            manager->MoveItemSlot(srcType, (ushort)srcSlot, dstType, (ushort)dstSlot, false);
            moveItem = itemId;
            moveQty = srcQty;
            moveDstType = dstType;
            moveDstSlot = dstSlot;
            stepDeadline = now + 4000;
            moveStep = MoveStep.WaitMove;
        }
        else
        {
            if (!EzThrottler.Throttle("EE.Op", 350)) return false;
            manager->SplitItem(srcType, (ushort)srcSlot, remaining);
            moveItem = itemId;
            splitWant = remaining;
            stepDeadline = now + 4000;
            moveStep = MoveStep.WaitSplit;
        }
        return false;
    }

    /// <summary>Largest bag stack not exceeding the wanted amount, else the smallest larger one (to split).</summary>
    private static unsafe bool PickSourceStack(uint itemId, int wanted, out InventoryType type, out int slotIndex, out int quantity)
    {
        var manager = InventoryManager.Instance();
        type = default;
        slotIndex = -1;
        quantity = 0;
        var bestUnder = -1;
        var bestOver = int.MaxValue;
        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId) continue;
                if (slot->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)) continue;
                var qty = (int)slot->Quantity;
                if (qty <= wanted && qty > bestUnder)
                {
                    bestUnder = qty;
                    type = bag;
                    slotIndex = i;
                    quantity = qty;
                }
                else if (qty > wanted && bestUnder < 0 && qty < bestOver)
                {
                    bestOver = qty;
                    type = bag;
                    slotIndex = i;
                    quantity = qty;
                }
            }
        }
        return slotIndex >= 0;
    }

    private static unsafe bool FindBagStack(uint itemId, int exactQuantity, out InventoryType type, out int slotIndex)
    {
        var manager = InventoryManager.Instance();
        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId && slot->Quantity == exactQuantity)
                {
                    type = bag;
                    slotIndex = i;
                    return true;
                }
            }
        }
        type = default;
        slotIndex = -1;
        return false;
    }

    private static unsafe bool FindEmptyRetainerSlot(out InventoryType type, out int slotIndex)
    {
        var manager = InventoryManager.Instance();
        foreach (var page in RetainerPages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0)
                {
                    type = page;
                    slotIndex = i;
                    return true;
                }
            }
        }
        type = default;
        slotIndex = -1;
        return false;
    }

    // ---- Helpers --------------------------------------------------------------

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

    private static string ItemNameOf(uint itemId)
        => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item {itemId}";

    private static void Print(string message) => Svc.Chat.Print($"[ExcessEntrust] {message}");
}
