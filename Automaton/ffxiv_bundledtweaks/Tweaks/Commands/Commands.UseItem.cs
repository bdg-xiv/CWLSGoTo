using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/item")]
    public bool EnableUseItem = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler("/item", "Use an item by ID", nameof(Config.EnableUseItem))]
    internal unsafe void OnCommandUseItem(string command, string arguments) {
        if (!uint.TryParse(arguments, out var itemId)) return;
        var agent = ActionManager.Instance();
        if (agent == null) return;
        var item = ItemUtil.GetBaseId(itemId);
        agent->UseAction(item.Kind is ItemKind.EventItem ? ActionType.EventItem : ActionType.Item, itemId, extraParam: 65535);
    }
}

