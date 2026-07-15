using System.Threading.Tasks;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/gotoflag")]
    public bool EnableGoToFlag = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [Requires(Ipc.Navmesh)]
    [CommandHandler(["/gotoflag", "/gtf"], "Goes to flag location", nameof(Config.EnableGoToFlag))]
    internal void OnGoToFlagCommand(string _, string __) => Svc.Automation.Start(new GoToFlagTask());

    private class GoToFlagTask : TaskBase {
        protected override async Task Execute() => await MoveToFlag(MovementConfig.Everything);
    }
}

