using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Orchestrion;

public class DalamudApi
{
    public static void Initialize(IDalamudPluginInterface pluginInterface) => pluginInterface.Create<DalamudApi>();

    #pragma warning disable CS8618
    // [PluginService] public static IAetheryteList AetheryteList { get; private set; }
    // [PluginService] public static IBuddyList BuddyList { get; private set; }
    [PluginService] public static IChatGui ChatGui { get; private set; }
    [PluginService] public static IClientState ClientState { get; private set; }
    [PluginService] public static ICommandManager CommandManager { get; private set; }
    [PluginService] public static ICondition Condition { get; private set; }
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; }
    [PluginService] public static IDataManager DataManager { get; private set; }
    [PluginService] public static IDtrBar DtrBar { get; private set; }
    // [PluginService] public static IFateTable FateTable { get; private set; }
    // [PluginService] public static IFlyTextGui FlyTextGui { get; private set; }
    [PluginService] public static IFramework Framework { get; private set; }
    [PluginService] public static IGameConfig GameConfig { get; private set; }
    [PluginService] public static IGameGui GameGui { get; private set; }
    [PluginService] public static IGameInteropProvider Hooks { get; private set; }
    // [PluginService] public static IGameNetwork GameNetwork { get; private set; }
    // [PluginService] public static IGamepadState GamePadState { get; private set; }
    // [PluginService] public static IJobGauges JobGauges { get; private set; }
    // [PluginService] public static IKeyState KeyState { get; private set; }
    // [PluginService] public static ILibcFunction LibcFunction { get; private set; }
    // [PluginService] public static IObjectTable ObjectTable { get; private set; }
    // [PluginService] public static IPartyFinderGui PartyFinderGui { get; private set; }
    // [PluginService] public static IPartyList PartyList { get; private set; }
    [PluginService] public static IPluginLog PluginLog { get; private set; }
    [PluginService] public static ISigScanner SigScanner { get; private set; }
    // [PluginService] public static ITargetManager TargetManager { get; private set; }
    // [PluginService] public static IToastGui ToastGui { get; private set; }
    // [PluginService] public static ITitleScreenMenu TitleScreenMenu { get; private set; }
    #pragma warning restore CS8618
}