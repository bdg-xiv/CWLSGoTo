using FFXIVClientStructs.FFXIV.Client.Game;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/tpflag")]
    public bool EnableTPFlag = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler(["/tpf", "/tpflag"], "Teleport to the aetheryte nearest your flag", nameof(Config.EnableTPFlag))]
    internal void OnCommmandTeleportFlag(string _, string __) {
        if (Coords.FindClosestAetheryteToFlag(false) is { } aetheryte)
            ActionManager.Teleport(aetheryte);
    }
}

