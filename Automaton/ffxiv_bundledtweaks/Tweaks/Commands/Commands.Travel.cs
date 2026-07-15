using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/travel")]
    public bool EnableTravel = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler("/travel", "Invoke world travel. Still have to be in a starting city.", nameof(Config.EnableTravel))]
    private unsafe void OnTravelCommand(string command, string arguments) => AgentWorldTravel.Instance()->Travel(arguments);
}

