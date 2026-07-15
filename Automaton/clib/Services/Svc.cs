using AllaganLib.GameSheets.Service;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Collections.Concurrent;

namespace clib.Services;

public class Svc {
    [PluginService] public static IAddonEventManager AddonEventManager { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] public static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] public static IBuddyList Buddies { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IDutyState DutyState { get; private set; } = null!;
    [PluginService] public static IFateTable Fates { get; private set; } = null!;
    [PluginService] public static IFlyTextGui FlyText { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] public static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] public static IGameLifecycle GameLifecycle { get; private set; } = null!;
    [PluginService] public static IGamepadState GamepadState { get; private set; } = null!;
    [PluginService] public static IJobGauges Gauges { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] public static INamePlateGui NamePlates { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static IPartyFinderGui PfGui { get; private set; } = null!;
    [PluginService] public static IPartyList Party { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IReliableFileStorage ReliableFileStorage { get; private set; } = null!;
    [PluginService] public static ISeStringEvaluator SeStringEvaluator { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static ITargetManager Targets { get; private set; } = null!;
    [PluginService] public static ITextureProvider Texture { get; private set; } = null!;
    [PluginService] public static ITextureSubstitutionProvider TextureSubstitution { get; private set; } = null!;
    [PluginService] public static ITextureReadbackProvider TextureReadback { get; private set; } = null!;
    [PluginService] public static ITitleScreenMenu TitleScreenMenu { get; private set; } = null!;
    [PluginService] public static IToastGui Toasts { get; private set; } = null!;
    [PluginService] public static IUnlockState UnlockState { get; private set; } = null!;

    public static ArmoireService Armoire { get; private set; } = null!;
    public static Automation Automation { get; private set; } = null!;
    public static SheetManager SheetManager { get; private set; } = null!;

    internal static NavmeshIPC Navmesh { get; private set; } = null!;

    private static readonly ConcurrentDictionary<Type, object> Singletons = new();

    public static void Register<T>() where T : class, new()
        => Register(() => new T());

    public static void Register<T>(Func<T> singleton) where T : class {
        ArgumentNullException.ThrowIfNull(singleton);
        var key = typeof(T);
        var instance = singleton();
        if (!Singletons.TryAdd(key, instance))
            throw new InvalidOperationException($"[{nameof(Svc)}] {key.FullName} is already registered.");
    }

    public static T Get<T>() where T : class {
        if (!Singletons.TryGetValue(typeof(T), out var instance))
            throw new InvalidOperationException($"[{nameof(Svc)}] {typeof(T).FullName} has not been registered.");
        return (T)instance;
    }

    internal static void Init(IDalamudPluginInterface pi, CLibModule modules) {
        pi.Create<Svc>();
        Navmesh = new NavmeshIPC();

        if (modules.HasFlag(CLibModule.Armoire))
            Armoire = new();
        if (modules.HasFlag(CLibModule.Automation))
            Automation = new();
        if (modules.HasFlag(CLibModule.SheetManager))
            SheetManager = new(pi, Data.GameData, new());
    }

    internal static void Dispose() {
        Armoire?.Dispose();
        Automation?.Dispose();
        SheetManager?.Dispose();

        foreach (var s in Singletons.Values) {
            if (s is not IDisposable d) continue;
            try {
                d.Dispose();
            }
            catch {
                Log.Error($"[{nameof(Svc)}] Failed disposal of {d.GetType().FullName}");
            }
        }
    }
}

internal static class LogExtensions {
    public static void Print(this IPluginLog log, string message) => log.Debug($"[{nameof(clib)}] {message}");
    public static void PrintWarning(this IPluginLog log, string message) => log.Warning($"[{nameof(clib)}] {message}");
    public static void PrintError(this IPluginLog log, string message) => log.Error($"[{nameof(clib)}] {message}");
}
