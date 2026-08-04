using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using KamiToolKit;

namespace HuntSpots;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/huntspots";
    private const string UpdateThrottleName = "HuntSpotsUpdate";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("HuntSpots");
    private readonly MainWindow window;

    // Built on the first framework tick, not here: the plugin constructor runs on a worker
    // thread and KamiToolKit refuses to touch the map addon from anywhere but the main one.
    private MapLayer? mapLayer;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);
        KamiToolKitLibrary.Initialize(pluginInterface);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        window = new MainWindow(config);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Show A and S rank spawn points on the map.",
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();

        TearDownNativeUi();
        ECommonsMain.Dispose();
    }

    /// <summary>
    /// The map nodes have to come down on the main thread, and the assembly must not go away
    /// before that has happened - so when unload lands on a worker thread, wait for the tick.
    /// </summary>
    private void TearDownNativeUi()
    {
        var layer = mapLayer;
        mapLayer = null;

        void Teardown()
        {
            layer?.Dispose();

            // KamiToolKit installs a hook of its own during Initialize; without this the plugin
            // unloads still owning it, which Dalamud reports as a leaked hook.
            KamiToolKitLibrary.Cleanup();
        }

        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            Teardown();
            return;
        }

        try
        {
            Svc.Framework.RunOnFrameworkThread(Teardown).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to tear down the native UI");
        }
    }

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string arguments) => ToggleWindow();

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // We are on the main thread here, which is the only place the map layer is allowed
            // to be built or touched.
            mapLayer ??= new MapLayer();

            if (!EzThrottler.Throttle(UpdateThrottleName, 250))
                return;

            mapLayer.Sync(config);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
