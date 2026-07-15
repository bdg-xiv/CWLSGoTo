using ComplexTweaks.Tasks;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/killflag")]
    public bool EnableKillFlag = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [Requires(Ipc.BossMod | Ipc.Navmesh)]
    [CommandHandler(["/killflag", "/kf"], "Goes to flag, kills hunt mob at destination.", nameof(Config.EnableKillFlag))]
    internal void OnCommandKillFlag(string _, string arguments) => Svc.Automation.Start(new KillFlag(arguments));
}

