using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/desynth")]
    public bool EnableDesynth = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler("/desynth", "Desynth an item by ID", nameof(Config.EnableDesynth))]
    internal unsafe void OnCommmandDesynth(string command, string arguments) {
        if (!uint.TryParse(arguments, out var itemId)) return;
        var item = new ItemHandle(itemId);
        if (!item.TrySetItemLocation()) {
            DuoLog.Error($"Failed to find item {item} in inventory");
            return;
        }

        if (item.GameData.Value.Desynth == 0) {
            DuoLog.Error($"Item {item} is not desynthable");
            return;
        }

        AgentSalvage.Instance()->SalvageItem(item.ItemLocation.GetInventoryItem());
        var retval = new AtkValue();
        Span<AtkValue> param = [
            new AtkValue { Type = AtkValueType.Int, Int = 0 },
            new AtkValue { Type = AtkValueType.Bool, Byte = 1 }
        ];
        AgentSalvage.Instance()->AgentInterface.ReceiveEvent(&retval, param.GetPointer(0), 2, 1);
    }
}

