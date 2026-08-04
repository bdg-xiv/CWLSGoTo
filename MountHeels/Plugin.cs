using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;

namespace MountHeels;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mountheels";
    private const string UpdateThrottleName = "MountHeelsUpdate";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly Configuration config;
    private readonly SimpleHeelsIpc heels;
    private readonly MountWatcher watcher;
    private readonly WindowSystem windows = new("MountHeels");
    private readonly MainWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        heels = new SimpleHeelsIpc();
        watcher = new MountWatcher(config, heels);
        window = new MainWindow(config, watcher, heels);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;
        // Logging out invalidates the registration, and leaving it in place would have Simple
        // Heels holding a config for an object index that is now somebody else.
        Svc.ClientState.Logout += OnLogout;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Set a Simple Heels offset for the mount you are on.",
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        Svc.ClientState.Logout -= OnLogout;
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();

        // Never leave Simple Heels holding a config of ours after we are gone.
        try
        {
            watcher.Release();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to hand control back to Simple Heels");
        }

        ECommonsMain.Dispose();
    }

    private void OnLogout(int type, int code) => watcher.Release();

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string arguments) => ToggleWindow();

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (!EzThrottler.Throttle(UpdateThrottleName, 200))
                return;

            watcher.Update();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
