using FFXIVClientStructs.FFXIV.Client.Game;
using System.Threading.Tasks;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/lowerquality")]
    public bool EnableLowerQuality = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler("/lowerquality", "Lower the quality of an item by ID, or pass all", nameof(Config.EnableLowerQuality))]
    internal void OnCommmandLowerQuality(string _, string arguments) {
        if (!uint.TryParse(arguments, out var itemId) && arguments != "all") return;
        if (arguments == "all") {
            Svc.Automation.Start(new LowerQualityAll());
        }
        else {
            if (new ItemHandle(itemId) is ItemHandle item && item.TrySetItemLocation(InventoryItem.ItemFlags.HighQuality)) {
                Log($"Lowering quality on item [{item}] in {item.ItemLocation}");
                item.LowerItemQuality();
            }
        }
    }

    private class LowerQualityAll : TaskBase {
        protected override async Task Execute() {
            foreach (var i in InventoryManager.GetHqItems(InventoryType.Bags)) {
                while (!i.LowerItemQuality())
                    await NextFrame();
            }
        }
    }
}

