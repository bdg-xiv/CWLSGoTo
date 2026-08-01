using System;
using Dalamud.Game.Chat;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using KamiToolKit;

namespace OccultCoffers;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/coffers";
    private const string UpdateThrottleName = "OccultCoffersUpdate";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly WindowSystem windows = new("OccultCoffers");
    private readonly MainWindow window;

    // Built on the first framework tick, not here: the plugin constructor runs on a worker
    // thread and KamiToolKit refuses to touch the map addon from anywhere but the main one.
    private MapLayer? mapLayer;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);
        KamiToolKitLibrary.Initialize(pluginInterface);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        tracker = new Tracker(config);
        window = new MainWindow(tracker, config);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.Chat.ChatMessage += OnChatMessage;

        Svc.Commands.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Show what Occult Treasuresight found and which spots are left.",
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();

        DisposeMapLayer();
        ECommonsMain.Dispose();
    }

    /// <summary>
    /// The map nodes have to come down on the main thread, and the assembly must not go away
    /// before that has happened - so when unload lands on a worker thread, wait for the tick.
    /// </summary>
    private void DisposeMapLayer()
    {
        var layer = mapLayer;
        mapLayer = null;
        if (layer == null)
            return;

        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            layer.Dispose();
            return;
        }

        try
        {
            Svc.Framework.RunOnFrameworkThread(layer.Dispose).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to tear down the map layer");
        }
    }

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string arguments) => ToggleWindow();

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (!config.Enabled)
                return;

            var text = message.Message.TextValue;
            if (!tracker.TryHandleMessage(text))
                return;

            if (config.OpenWindowOnSight)
                window.IsOpen = true;

            Announce();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to handle a chat message");
        }
    }

    private void Announce()
    {
        var silver = tracker.Reported(CofferKind.Silver);
        var bronze = tracker.Reported(CofferKind.Bronze);
        var spots = tracker.SpotsLoaded ? tracker.Spots.Count : 0;
        Svc.Chat.Print($"[Occult Coffers] {silver} silver, {bronze} bronze - narrowing down from {spots} spots.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // We are on the main thread here, which is the only place the map layer is
            // allowed to be built or touched.
            mapLayer ??= new MapLayer();

            if (!config.Enabled)
            {
                mapLayer.Clear();
                return;
            }

            if (!EzThrottler.Throttle(UpdateThrottleName, 250))
                return;

            tracker.Update();
            mapLayer.Sync(tracker, config);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
