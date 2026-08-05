using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;

namespace PositionalBridge;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/posbridge";
    private const string UpdateThrottleName = "PositionalBridgeUpdate";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly Configuration config;
    private readonly Bridge bridge;
    private readonly WindowSystem windows = new("PositionalBridge");
    private readonly MainWindow window;

    private uint lastJob;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        bridge = new Bridge();
        window = new MainWindow(config, bridge);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Show what is being handed to Avarice.",
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

        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string arguments) => ToggleWindow();

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (!EzThrottler.Throttle(UpdateThrottleName, 100))
                return;

            bridge.Update(config);

            // Changing job invalidates the last answer, and Avarice would otherwise keep
            // drawing a Viper's positional for two seconds after switching off it.
            if (bridge.Job != lastJob)
            {
                lastJob = bridge.Job;
                bridge.Reset();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
