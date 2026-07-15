using clib.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Utils;

// small version of https://github.com/electr0sheep/ItemVendorLocation/ without npc association
public class ItemCostLookup {
    private readonly ExcelSheet<CollectablesShop> _collectablesShops;
    private readonly SubrowExcelSheet<CollectablesShopItem> _collectablesShopItems;
    private readonly ExcelSheet<CollectablesShopRefine> _collectablesShopRefines;
    private readonly ExcelSheet<CollectablesShopRewardItem> _collectablesShopRewardItems;
    private readonly ExcelSheet<ENpcBase> _eNpcBases;
    private readonly ExcelSheet<FateShop> _fateShops;
    private readonly ExcelSheet<FccShop> _fccShops;
    private readonly ExcelSheet<GCShop> _gcShops;
    private readonly ExcelSheet<GCScripShopCategory> _gcScripShopCategories;
    private readonly SubrowExcelSheet<GCScripShopItem> _gcScripShopItems;
    private readonly ExcelSheet<GilShop> _gilShops;
    private readonly SubrowExcelSheet<GilShopItem> _gilShopItems;
    private readonly ExcelSheet<InclusionShop> _inclusionShops;
    private readonly SubrowExcelSheet<InclusionShopSeries> _inclusionShopSeries;
    private readonly ExcelSheet<Item> _items;
    private readonly ExcelSheet<PreHandler> _preHandlers;
    private readonly SubrowExcelSheet<QuestClassJobReward> _questClassJobRewards;
    private readonly ExcelSheet<SpecialShop> _specialShops;
    private readonly ExcelSheet<TopicSelect> _topicSelects;

    private readonly Item _gil;
    private readonly List<Item> _gcSeal;
    private static readonly Dictionary<uint, uint> Currencies = new()
    {
        { 1, 28 },
        { 2, 33913 },
        { 4, 33914 },
        { 6, 41784 },
        { 7, 41785 },
    };

    private static readonly Dictionary<uint, uint> ShbFateShopNpc = new()
    {
        { 1027998, 1769957 },
        { 1027538, 1769958 },
        { 1027385, 1769959 },
        { 1027497, 1769960 },
        { 1027892, 1769961 },
        { 1027665, 1769962 },
        { 1027709, 1769963 },
        { 1027766, 1769964 },
    };

    public ItemCostLookup() {
        _collectablesShops = Svc.Data.GetExcelSheet<CollectablesShop>();
        _collectablesShopItems = Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>();
        _collectablesShopRefines = Svc.Data.GetExcelSheet<CollectablesShopRefine>();
        _collectablesShopRewardItems = Svc.Data.GetExcelSheet<CollectablesShopRewardItem>();
        _eNpcBases = Svc.Data.GetExcelSheet<ENpcBase>();
        _fateShops = Svc.Data.GetExcelSheet<FateShop>();
        _fccShops = Svc.Data.GetExcelSheet<FccShop>();
        _gcShops = Svc.Data.GetExcelSheet<GCShop>();
        _gcScripShopCategories = Svc.Data.GetExcelSheet<GCScripShopCategory>();
        _gcScripShopItems = Svc.Data.GetSubrowExcelSheet<GCScripShopItem>();
        _gilShops = Svc.Data.GetExcelSheet<GilShop>();
        _gilShopItems = Svc.Data.GetSubrowExcelSheet<GilShopItem>();
        _inclusionShops = Svc.Data.GetExcelSheet<InclusionShop>();
        _inclusionShopSeries = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
        _items = Svc.Data.GetExcelSheet<Item>();
        _preHandlers = Svc.Data.GetExcelSheet<PreHandler>();
        _questClassJobRewards = Svc.Data.GetSubrowExcelSheet<QuestClassJobReward>();
        _specialShops = Svc.Data.GetExcelSheet<SpecialShop>();
        _topicSelects = Svc.Data.GetExcelSheet<TopicSelect>();

        _gil = _items.GetRow(1);
        _gcSeal = [.. _items.Where(i => i.RowId is >= 20 and <= 22).Select(i => i)];

        BuildAllItems();
    }

    public sealed record ItemCost(ItemHandle Item, List<(ItemHandle CurrencyItem, uint Amount)> Costs) {
        public override string ToString() => $"{string.Join(", ", Costs.Select(c => $"{c.Amount}x {c.CurrencyItem.GameData.Value.Name}"))}";
    }

    public ItemCost? GetItemCost(uint itemId) {
        if (!AllItemCosts.TryGetValue(itemId, out var costs) || costs.Count == 0) {
            return null;
        }

        var itemHandle = new ItemHandle(itemId);
        var costList = costs.Select(c => (new ItemHandle(c.ItemId), c.Amount)).ToList();
        return new ItemCost(itemHandle, costList);
    }

    public List<(uint ItemId, uint Amount)> GetItemCosts(uint itemId) => AllItemCosts.TryGetValue(itemId, out var costs) ? costs : [];
    public Dictionary<uint, List<(uint ItemId, uint Amount)>> AllItemCosts { get; } = [];

    private void BuildAllItems() {
        var processedShops = new HashSet<uint>();

        foreach (var npcBase in _eNpcBases)
            BuildVendorInfo(npcBase, processedShops);

        AddAchievementItems();

        // Process SpecialShops that weren't processed via NPCs
        foreach (var shop in _specialShops) {
            if (shop.RowId == 0 || processedShops.Contains(shop.RowId))
                continue;
            AddSpecialItem(shop);
        }
    }

    private void BuildVendorInfo(ENpcBase npcBase, HashSet<uint> processedShops) {
        if (FixNpcVendorInfo(npcBase, processedShops)) {
            return;
        }

        var fateShop = _fateShops.GetRowOrDefault(npcBase.RowId);
        if (fateShop.HasValue) {
            foreach (var specialShop in fateShop.Value.SpecialShop) {
                var specialShopCustom = _specialShops.GetRowOrDefault(specialShop.RowId);
                if (specialShopCustom == null) {
                    continue;
                }

                processedShops.Add(specialShopCustom.Value.RowId);
                AddSpecialItem(specialShopCustom.Value);
            }

            return;
        }

        foreach (var npcData in npcBase.ENpcData.Select(x => x.RowId)) {
            if (npcData == 0) {
                break;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.CollectablesShop)) {
                var collectablesShop = _collectablesShops.GetRow(npcData);
                AddCollectablesShop(collectablesShop);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.InclusionShop)) {
                var inclusionShop = _inclusionShops.GetRow(npcData);
                AddInclusionShop(inclusionShop, processedShops);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.FcShop)) {
                var fccShop = _fccShops.GetRow(npcData);
                AddFccShop(fccShop);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.PreHandler)) {
                var preHandler = _preHandlers.GetRow(npcData);
                AddItemsInPrehandler(preHandler, processedShops);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.TopicSelect)) {
                var topicSelect = _topicSelects.GetRow(npcData);
                AddItemsInTopicSelect(topicSelect, processedShops);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.GcShop)) {
                var gcShop = _gcShops.GetRow(npcData);
                AddGcShopItem(gcShop);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.SpecialShop)) {
                var specialShop = _specialShops.GetRow(npcData);
                processedShops.Add(specialShop.RowId);
                AddSpecialItem(specialShop);
                continue;
            }

            if (MatchEventHandlerType(npcData, EventHandlerType.GilShop)) {
                var gilShop = _gilShops.GetRow(npcData);
                AddGilShopItem(gilShop);
                continue;
            }
        }
    }

    private void AddSpecialItem(SpecialShop specialShop) {
        foreach (var entry in specialShop.Item) {
            for (var i = 0; i < entry.ReceiveItems.Count; i++) {
                var item = entry.ReceiveItems[i].Item.Value;
                var costs = (from e in entry.ItemCosts
                             where e.ItemCost.IsValid && e.ItemCost.Value.RowId != 0 && e.ItemCost.Value.Name != string.Empty
                             select (ConvertCurrency(e.ItemCost.Value.RowId, specialShop).RowId, e.CurrencyCost)).ToList();

                AddItemCost(item.RowId, costs);
            }
        }
    }

    private void AddGilShopItem(GilShop gilShop) {
        for (ushort i = 0; ; i++) {
            try {
                var item = _gilShopItems.GetSubrowOrDefault(gilShop.RowId, i);
                if (!item.HasValue) {
                    break;
                }

                AddItemCost(item.Value.Item.Value.RowId, [new(_gil.RowId, item.Value.Item.Value.PriceMid)]);
            }
            catch (Exception) {
                break;
            }
        }
    }

    private void AddGcShopItem(GCShop gcId) {
        var seal = _gcSeal.Find(i => i.Description.ExtractText().Contains($"{gcId.GrandCompany.Value.Name.ExtractText()}"));

        foreach (var category in _gcScripShopCategories.Where(i => i.GrandCompany.RowId == gcId.GrandCompany.RowId)) {
            for (ushort i = 0; ; i++) {
                try {
                    var item = _gcScripShopItems.GetSubrowOrDefault(category.RowId, i);
                    if (item == null) {
                        break;
                    }

                    if (item.Value.SortKey == 0) {
                        break;
                    }

                    AddItemCost(item.Value.Item.Value.RowId, [new(seal.RowId, item.Value.CostGCSeals)]);
                }
                catch (Exception) {
                    break;
                }
            }
        }
    }

    private void AddInclusionShop(InclusionShop inclusionShop, HashSet<uint> processedShops) {
        foreach (var category in inclusionShop.Category) {
            if (category.Value.RowId == 0) {
                continue;
            }

            for (ushort i = 0; ; i++) {
                try {
                    var series = _inclusionShopSeries.GetSubrowOrDefault(category.Value.InclusionShopSeries.RowId, i);
                    if (!series.HasValue) {
                        break;
                    }

                    var specialShop = series.Value.SpecialShop.Value;
                    processedShops.Add(specialShop.RowId);
                    AddSpecialItem(specialShop);
                }
                catch (Exception) {
                    break;
                }
            }
        }
    }

    private void AddFccShop(FccShop shop) {
        for (var i = 0; i < shop.ItemData.Count; i++) {
            var item = _items.GetRowOrDefault(shop.ItemData[i].Item.RowId);
            if (item == null || item.Value.Name == string.Empty) {
                continue;
            }

            var cost = shop.ItemData[i].Cost;
            // FC Credits don't have an item ID, using 0 as sentinel value
            AddItemCost(item.Value.RowId, [new(0, cost)]);
        }
    }

    private void AddItemsInPrehandler(PreHandler preHandler, HashSet<uint> processedShops) {
        var target = preHandler.Target.RowId;
        if (target == 0) {
            return;
        }

        if (MatchEventHandlerType(target, EventHandlerType.GilShop)) {
            var gilShop = _gilShops.GetRow(target);
            AddGilShopItem(gilShop);
            return;
        }

        if (MatchEventHandlerType(target, EventHandlerType.SpecialShop)) {
            var specialShop = _specialShops.GetRow(target);
            processedShops.Add(specialShop.RowId);
            AddSpecialItem(specialShop);
            return;
        }

        if (MatchEventHandlerType(target, EventHandlerType.InclusionShop)) {
            var inclusionShop = _inclusionShops.GetRow(target);
            AddInclusionShop(inclusionShop, processedShops);
            return;
        }
    }

    private void AddItemsInTopicSelect(TopicSelect topicSelect, HashSet<uint> processedShops) {
        foreach (var data in topicSelect.Shop.Select(x => x.RowId)) {
            if (data == 0) {
                continue;
            }

            if (MatchEventHandlerType(data, EventHandlerType.SpecialShop)) {
                var specialShop = _specialShops.GetRow(data);
                processedShops.Add(specialShop.RowId);
                AddSpecialItem(specialShop);
                continue;
            }

            if (MatchEventHandlerType(data, EventHandlerType.GilShop)) {
                var gilShop = _gilShops.GetRow(data);
                AddGilShopItem(gilShop);
                continue;
            }

            if (MatchEventHandlerType(data, EventHandlerType.PreHandler)) {
                var preHandler = _preHandlers.GetRow(data);
                AddItemsInPrehandler(preHandler, processedShops);
                continue;
            }
        }
    }

    private void AddCollectablesShop(CollectablesShop shop) {
        if (shop.Name.ExtractText() == string.Empty) {
            return;
        }

        for (var i = 0; i < shop.ShopItems.Count; i++) {
            var row = shop.ShopItems[i].Value.RowId;
            if (row == 0) {
                continue;
            }

            for (ushort subRow = 0; subRow < 100; subRow++) {
                try {
                    var exchangeItem = _collectablesShopItems.GetSubrow(row, subRow);
                    var rewardItem = _collectablesShopRewardItems.GetRow(exchangeItem.CollectablesShopRewardScrip.RowId);
                    var refine = _collectablesShopRefines.GetRow(exchangeItem.CollectablesShopRefine.RowId);

                    if (exchangeItem.Item.RowId <= 1000) {
                        continue;
                    }

                    var costs = new List<(uint ItemId, uint Amount)>();
                    if (rewardItem.RewardLow > 0)
                        costs.Add(new(exchangeItem.Item.RowId, rewardItem.RewardLow));

                    if (rewardItem.RewardMid > 0)
                        costs.Add(new(exchangeItem.Item.RowId, rewardItem.RewardMid));

                    if (rewardItem.RewardHigh > 0)
                        costs.Add(new(exchangeItem.Item.RowId, rewardItem.RewardHigh));

                    AddItemCost(rewardItem.Item.Value.RowId, costs);
                }
                catch {
                    break;
                }
            }
        }
    }

    private void AddQuestReward(QuestClassJobReward questReward, List<(uint ItemId, uint Amount)>? cost = null) {
        if (questReward.ClassJobCategory.RowId == 0) {
            return;
        }

        if (cost == null) {
            cost = [];
            for (var i = 0; i < questReward.RequiredItem.Count; i++) {
                var requireItem = questReward.RequiredItem[i];
                if (requireItem.RowId == 0) {
                    break;
                }

                cost.Add(new(requireItem.RowId, questReward.RequiredAmount[i]));
            }
        }

        for (var i = 0; i < questReward.RewardItem.Count; i++) {
            var rewardItem = questReward.RewardItem[i];
            if (rewardItem.RowId == 0) {
                break;
            }

            AddItemCost(rewardItem.RowId, cost);
        }
    }

    private void AddQuestRewardCost(QuestClassJobReward questReward, List<(uint ItemId, uint Amount)> cost) {
        if (cost == null || questReward.ClassJobCategory.RowId == 0) {
            return;
        }

        for (var i = 0; i < questReward.RewardItem.Count; i++) {
            var rewardItem = questReward.RewardItem[i];
            if (rewardItem.RowId == 0) {
                break;
            }

            AddItemCost(rewardItem.RowId, cost);
        }
    }

    private void AddAchievementItems() {
        for (var i = 1006004u; i <= 1006006; i++) {
            //var npcBase = _eNpcBases.GetRow(i);

            for (var j = 1769898u; j <= 1769906; j++) {
                AddSpecialItem(_specialShops.GetRow(j));
            }
        }
    }

    private void AddItemCost(uint itemId, List<(uint ItemId, uint Amount)> costs) {
        if (itemId == 0 || costs == null || costs.Count == 0) {
            return;
        }

        if (!AllItemCosts.ContainsKey(itemId)) {
            AllItemCosts[itemId] = [];
        }

        // Add all costs, avoiding duplicates
        foreach (var cost in costs) {
            if (!AllItemCosts[itemId].Any(c => c.ItemId == cost.ItemId && c.Amount == cost.Amount)) {
                AllItemCosts[itemId].Add(cost);
            }
        }
    }

    private bool FixNpcVendorInfo(ENpcBase npcBase, HashSet<uint> processedShops) {
        switch (npcBase.RowId) {
            case 1043463: // horrendous hoarder
                AddSpecialItem(_specialShops.GetRow(1770601));
                processedShops.Add(1770601);
                AddSpecialItem(_specialShops.GetRow(1770659));
                processedShops.Add(1770659);
                AddSpecialItem(_specialShops.GetRow(1770660));
                processedShops.Add(1770660);
                AddSpecialItem(_specialShops.GetRow(1770602));
                processedShops.Add(1770602);
                AddSpecialItem(_specialShops.GetRow(1770603));
                processedShops.Add(1770603);
                AddSpecialItem(_specialShops.GetRow(1770723));
                processedShops.Add(1770723);
                AddSpecialItem(_specialShops.GetRow(1770734));
                processedShops.Add(1770734);
                return true;

            case 1018655: // disreputable priest
                AddSpecialItem(_specialShops.GetRow(1769743));
                processedShops.Add(1769743);
                AddSpecialItem(_specialShops.GetRow(1769744));
                processedShops.Add(1769744);
                AddSpecialItem(_specialShops.GetRow(1770537));
                processedShops.Add(1770537);
                return true;

            case 1016289: // syndony
                AddSpecialItem(_specialShops.GetRow(1769635));
                processedShops.Add(1769635);
                return true;

            case 1025047: // gerolt but in eureka
                for (uint i = 1769820; i <= 1769834; i++) {
                    AddSpecialItem(_specialShops.GetRow(i));
                    processedShops.Add(i);
                }

                return true;

            case 1025763: // doman junkmonger
                AddGilShopItem(_gilShops.GetRow(262919));
                return true;

            case 1027123: // eureka expedition artisan
                AddSpecialItem(_specialShops.GetRow(1769934));
                processedShops.Add(1769934);
                AddSpecialItem(_specialShops.GetRow(1769935));
                processedShops.Add(1769935);
                return true;

            case 1027124: // eureka expedition scholar
                AddSpecialItem(_specialShops.GetRow(1769937));
                processedShops.Add(1769937);
                return true;

            case 1033921: // faux
                AddSpecialItem(_specialShops.GetRow(1770282));
                processedShops.Add(1770282);
                return true;

            case 1035012: // Emeny
                for (ushort i = 0; i <= 10; i++) {
                    var questClassJobReward = _questClassJobRewards.GetSubrow(14, i);
                    AddQuestReward(questClassJobReward);
                    questClassJobReward = _questClassJobRewards.GetSubrow(15, i);
                    AddQuestReward(questClassJobReward);
                    questClassJobReward = _questClassJobRewards.GetSubrow(19, i);
                    AddQuestReward(questClassJobReward);
                }

                return true;

            case 1016135: // Ardashir
                static List<(uint ItemId, uint Amount)>? GetCost(uint idx) => idx switch {
                    3 =>
                    [
                        new(13575u, 1),
                            new(13576u, 1),
                        ],
                    5 =>
                    [
                        new(13577u, 1),
                            new(13578u, 1),
                            new(13579u, 1),
                            new(13580u, 1),
                        ],
                    6 =>
                    [
                        new(14899u, 5),
                        ],
                    7 =>
                    [
                        new(15840u, 60),
                            new(15841u, 60),
                        ],
                    8 =>
                    [
                        new(16064u, 50),
                        ],
                    9 =>
                    [
                        new(16932u, 1),
                        ],
                    10 =>
                    [
                        new(16934u, 1),
                        ],
                    _ => null
                };

                for (uint i = 3; i <= 10; i++) {
                    for (ushort j = 0; j <= 12; j++) {
                        var questClassJobReward = _questClassJobRewards.GetSubrow(i, j);
                        AddQuestReward(questClassJobReward);
                        if (GetCost(i) is { } cost)
                            AddQuestRewardCost(questClassJobReward, cost);
                    }
                }

                return true;

            case 1032903: // gerolt Resistance Weapons
                for (ushort i = 0; i <= 16; i++) {
                    var questClassJobReward = _questClassJobRewards.GetSubrow(12, i);
                    AddQuestReward(questClassJobReward,
                    [
                        new(30273u, 4),
                    ]);
                }

                return true;

            case 1032905: // Zlatan
                for (ushort i = 0; i <= 16; i++) {
                    var questClassJobReward = _questClassJobRewards.GetSubrow(13, i);
                    AddQuestReward(questClassJobReward,
                    [
                        new(30273u, 4),
                    ]);
                }

                for (ushort i = 0; i <= 16; i++) {
                    var questClassJobReward = _questClassJobRewards.GetSubrow(17, i);
                    AddQuestReward(questClassJobReward);
                    AddQuestRewardCost(questClassJobReward,
                    [
                        new(31573u, 20),
                        new(31574u, 20),
                        new(31575u, 20),
                    ]);

                    questClassJobReward = _questClassJobRewards.GetSubrow(18, i);
                    AddQuestReward(questClassJobReward);
                    AddQuestRewardCost(questClassJobReward,
                    [
                        new(31576u, 6)
                    ]);

                    questClassJobReward = _questClassJobRewards.GetSubrow(20, i);
                    AddQuestReward(questClassJobReward);
                    AddQuestRewardCost(questClassJobReward,
                    [
                        new(32956u, 15)
                    ]);

                    questClassJobReward = _questClassJobRewards.GetSubrow(21, i);
                    AddQuestReward(questClassJobReward);
                    AddQuestRewardCost(questClassJobReward,
                    [
                        new(32959u, 15)
                    ]);

                    questClassJobReward = _questClassJobRewards.GetSubrow(22, i);
                    AddQuestReward(questClassJobReward);
                    AddQuestRewardCost(questClassJobReward,
                    [
                        new(33767u, 15)
                    ]);
                }

                return true;

            default:
                if (!ShbFateShopNpc.TryGetValue(npcBase.RowId, out var value)) {
                    return false;
                }

                AddSpecialItem(_specialShops.GetRow(value));
                processedShops.Add(value);
                return true;
        }
    }

    private Item ConvertCurrency(uint itemId, SpecialShop specialShop) {
        var tomestonesItemSheet = Svc.Data.GetExcelSheet<TomestonesItem>();
        var itemSheet = Svc.Data.GetExcelSheet<Item>();
        var useCurrencyType = specialShop.UseCurrencyType;

        // hack for Quinnana's special shops
        if (specialShop.RowId is 1770637 or 1770638) {
            useCurrencyType = 16;
        }

        return itemId is >= 8 or 0
            ? itemSheet.GetRow(itemId)
            : useCurrencyType switch {
                16 => itemSheet.GetRow(Currencies[itemId]),
                8 => itemSheet.GetRow(1),
                4 => itemSheet.GetRow(tomestonesItemSheet.First(i => i.Tomestones.Value.RowId == itemId).Item.RowId),
                _ => itemSheet.GetRow(itemId),
            };
    }

    private static bool MatchEventHandlerType(uint data, EventHandlerType type) => (data >> 16) == (uint)type;

    internal enum EventHandlerType : uint {
        GilShop = 0x0004,
        CustomTalk = 0x000B,
        GcShop = 0x0016,
        SpecialShop = 0x001B,
        FcShop = 0x002A,
        TopicSelect = 0x0032,
        PreHandler = 0x0036,
        InclusionShop = 0x003a,
        CollectablesShop = 0x003B,
    }
}
