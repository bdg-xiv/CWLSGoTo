using clib.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace clib.Utils;

public static class ItemCost {
    public sealed class CostEntry {
        public required List<CostItem> Items { get; init; }
        public string? ShopName { get; init; }

        public override string ToString() => $"{string.Join(", ", Items)}";
    }

    public sealed class CostItem {
        public required uint ItemId { get; init; }
        public required uint Quantity { get; init; }
        public string? ItemName { get; init; }

        public override string ToString() => $"#{ItemId} {ItemName} {Quantity}x";
    }

    public static List<CostEntry> GetItemCosts(uint itemId) {
        var costs = new List<CostEntry>();
        costs.AddRange(GetSpecialShopCosts(itemId));
        costs.AddRange(GetGilShopCosts(itemId));
        costs.AddRange(GetGCShopCosts(itemId));
        costs.AddRange(GetCollectablesShopCosts(itemId));
        return costs;
    }

    private static List<CostEntry> GetSpecialShopCosts(uint itemId) {
        var costs = new List<CostEntry>();
        var specialShops = Svc.Data.GetSheet<SpecialShop>();

        foreach (var shop in specialShops.Where(s => s.RowId > 0 && !string.IsNullOrEmpty(s.Name.ToString()))) {
            foreach (var shopItem in shop.Item) {
                foreach (var receiveItem in shopItem.ReceiveItems) {
                    if (receiveItem.Item.RowId != itemId)
                        continue;

                    var costItems = new List<CostItem>();
                    foreach (var itemCost in shopItem.ItemCosts) {
                        if (itemCost.ItemCost.RowId == 0)
                            continue;

                        var costItemId = itemCost.ItemCost.RowId;
                        var costQuantity = itemCost.CurrencyCost;
                        string? costName = null;

                        if (itemCost.CostType == 3) {
                            unsafe {
                                var specialId = CurrencyManager.Instance()->GetItemIdBySpecialId((byte)costItemId);
                                var item = Svc.Data.GetRow<Item>(specialId);
                                costName = item?.Name.ToString();
                            }
                        }
                        else {
                            var item = itemCost.ItemCost.Value;
                            costName = item.Name.ToString();
                        }

                        costItems.Add(new CostItem {
                            ItemId = costItemId,
                            Quantity = costQuantity,
                            ItemName = costName
                        });
                    }

                    if (costItems.Count > 0) {
                        costs.Add(new CostEntry {
                            Items = costItems,
                            ShopName = shop.Name.ToString()
                        });
                    }
                }
            }
        }

        return costs;
    }

    private static List<CostEntry> GetGilShopCosts(uint itemId) {
        var costs = new List<CostEntry>();
        var gilShops = Svc.Data.GetSheet<GilShop>();
        var gilShopItems = Svc.Data.GetSubrowSheet<GilShopItem>();
        var gil = Svc.Data.GetRow<Item>(1);

        foreach (var shop in gilShops.Where(s => s.RowId > 0)) {
            for (ushort i = 0; ; i++) {
                var shopItem = gilShopItems.GetSubrowOrDefault(shop.RowId, i);
                if (!shopItem.HasValue)
                    break;

                if (shopItem.Value.Item.Value.RowId != itemId)
                    continue;

                costs.Add(new CostEntry {
                    Items = [
                        new() {
                            ItemId = 1,
                            Quantity = shopItem.Value.Item.Value.PriceMid,
                            ItemName = gil?.Name.ToString() ?? "Gil"
                        }
                    ],
                    ShopName = shop.Name.ToString()
                });
            }
        }

        return costs;
    }

    private static List<CostEntry> GetGCShopCosts(uint itemId) {
        var costs = new List<CostEntry>();
        var gcShops = Svc.Data.GetSheet<GCShop>();
        var gcScripShopCategories = Svc.Data.GetSheet<GCScripShopCategory>();
        var gcScripShopItems = Svc.Data.GetSubrowSheet<GCScripShopItem>();
        var gcSeals = Svc.Data.GetSheet<Item>().Where(i => i.RowId is >= 20 and <= 22).ToList();

        foreach (var shop in gcShops.Where(s => s.RowId > 0)) {
            if (gcSeals.FirstOrNull(i => i.Description.ToString().Contains(shop.GrandCompany.Value.Name.ToString())) is not { } seal)
                continue;

            foreach (var category in gcScripShopCategories.Where(c => c.GrandCompany.RowId == shop.GrandCompany.RowId)) {
                for (ushort i = 0; ; i++) {
                    var shopItem = gcScripShopItems.GetSubrowOrDefault(category.RowId, i);
                    if (shopItem == null || shopItem.Value.SortKey == 0)
                        break;

                    if (shopItem.Value.Item.Value.RowId != itemId)
                        continue;

                    costs.Add(new CostEntry {
                        Items = [
                            new() {
                                ItemId = seal.RowId,
                                Quantity = shopItem.Value.CostGCSeals,
                                ItemName = seal.Name.ToString()
                            }
                        ],
                        ShopName = null
                    });
                }
            }
        }

        return costs;
    }

    private static List<CostEntry> GetCollectablesShopCosts(uint itemId) {
        var costs = new List<CostEntry>();
        var collectablesShops = Svc.Data.GetSheet<CollectablesShop>();
        var collectablesShopItems = Svc.Data.GetSubrowSheet<CollectablesShopItem>();
        var collectablesShopRewardItems = Svc.Data.GetSheet<CollectablesShopRewardItem>();
        var collectablesShopRefines = Svc.Data.GetSheet<CollectablesShopRefine>();

        foreach (var shop in collectablesShops.Where(s => s.RowId > 0 && !string.IsNullOrEmpty(s.Name.ToString()))) {
            for (var shopItemIdx = 0; shopItemIdx < shop.ShopItems.Count; shopItemIdx++) {
                var shopItemRowId = shop.ShopItems[shopItemIdx].Value.RowId;
                if (shopItemRowId == 0)
                    continue;

                for (ushort subRow = 0; subRow < 100; subRow++) {
                    var exchangeItem = collectablesShopItems.GetSubrowOrDefault(shopItemRowId, subRow);
                    if (exchangeItem == null)
                        break;

                    if (exchangeItem.Value.Item.RowId <= 1000)
                        continue;

                    if (!collectablesShopRewardItems.TryGetRow(exchangeItem.Value.CollectablesShopRewardScrip.RowId, out var rewardItem))
                        continue;

                    if (rewardItem.Item.RowId != itemId)
                        continue;

                    if (!collectablesShopRefines.TryGetRow(exchangeItem.Value.CollectablesShopRefine.RowId, out var refine))
                        continue;

                    var exchangeItemName = exchangeItem.Value.Item.Value.Name.ToString();
                    var shopName = shop.Name.ToString();
                    if (!string.IsNullOrEmpty(exchangeItem.Value.CollectablesShopItemGroup.Value.Name.ToString())) {
                        shopName += $"\n{exchangeItem.Value.CollectablesShopItemGroup.Value.Name}";
                    }

                    if (rewardItem.RewardLow > 0) {
                        costs.Add(new CostEntry {
                            Items = [
                                new() {
                                    ItemId = exchangeItem.Value.Item.RowId,
                                    Quantity = rewardItem.RewardLow,
                                    ItemName = $"{exchangeItemName} (min collectability {refine.LowCollectability})"
                                }
                            ],
                            ShopName = shopName
                        });
                    }

                    if (rewardItem.RewardMid > 0) {
                        costs.Add(new CostEntry {
                            Items = [
                                new() {
                                    ItemId = exchangeItem.Value.Item.RowId,
                                    Quantity = rewardItem.RewardMid,
                                    ItemName = $"{exchangeItemName} (min collectability {refine.MidCollectability})"
                                }
                            ],
                            ShopName = shopName
                        });
                    }

                    if (rewardItem.RewardHigh > 0) {
                        costs.Add(new CostEntry {
                            Items = [
                                new() {
                                    ItemId = exchangeItem.Value.Item.RowId,
                                    Quantity = rewardItem.RewardHigh,
                                    ItemName = $"{exchangeItemName} (min collectability {refine.HighCollectability})"
                                }
                            ],
                            ShopName = shopName
                        });
                    }
                }
            }
        }

        return costs;
    }
}
