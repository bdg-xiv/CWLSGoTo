using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;

namespace GlamRoulette;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/glamroulette";
    private const string UpdateThrottleName = "GlamRouletteUpdate";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly Configuration config;
    private readonly GlamourerIpc glamourer;
    private readonly Wardrobe wardrobe;
    private readonly PlayerContextMenu contextMenu;
    private readonly WindowSystem windows = new("GlamRoulette");
    private readonly MainWindow window;
    private readonly PenumbraIpc penumbra;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        glamourer = new GlamourerIpc();
        var dyes = new Dyes(config, glamourer);
        var shoes = new Shoes(config);
        penumbra = new PenumbraIpc();
        var cplus = new CustomizePlusIpc();
        wardrobe = new Wardrobe(config, glamourer, dyes, new RaceSwap(config, glamourer),
            new ModRoulette(config, penumbra, dyes), new Exclusives(config, penumbra, dyes), penumbra,
            new Shapes(config, cplus), shoes);
        wardrobe.StampUnknownAsSeen();
        penumbra.OnRestart(OnPenumbraRestart);
        contextMenu = new PlayerContextMenu(config, wardrobe);
        window = new MainWindow(config, wardrobe, glamourer, dyes, penumbra, cplus, shoes);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Glam Roulette. 'reroll' for everyone, 'me' for yourself, 'off'/'on' to toggle.",
        });
    }

    /// <summary>Penumbra has restarted, so every mod and every temporary setting it was holding
    /// has been through a fresh start and none of what we remember about it still stands.</summary>
    private void OnPenumbraRestart()
    {
        Svc.Log.Information("[GlamRoulette] Penumbra restarted, working the mods out again");
        wardrobe.ForgetMods();
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        Svc.Framework.Update -= OnFrameworkUpdate;
        penumbra.StopWatching(OnPenumbraRestart);

        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();

        contextMenu.Dispose();

        // Leave nobody wearing something they did not choose once this is gone.
        try
        {
            wardrobe.RevertAll();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to put everyone back");
        }

        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => window.IsOpen = !window.IsOpen;

    private void OnCommand(string command, string arguments)
    {
        var argument = arguments.Trim().ToLowerInvariant();
        switch (argument)
        {
            case "":
                ToggleWindow();
                return;

            case "reroll":
                var count = wardrobe.RerollEverybody();
                var bodies = wardrobe.RerollBodies();
                Svc.Chat.Print($"[Glam Roulette] Re-rolling {count} remembered outfit(s)"
                               + (config.RandomizeShapes && bodies > 0 ? $" and {bodies} body/bodies." : "."));
                return;

            case "me":
            case "rerollme":
                Svc.Chat.Print(wardrobe.RerollMe()
                    ? "[Glam Roulette] Dealing yourself another one."
                    : "[Glam Roulette] Nothing of yours to re-roll - it may be one you chose to keep.");
                return;

            case "on":
            case "off":
                config.Enabled = argument == "on";
                config.Save();
                if (!config.Enabled)
                    wardrobe.RevertAll();
                Svc.Chat.Print($"[Glam Roulette] {(config.Enabled ? "On" : "Off")}.");
                return;

            case "revert":
                wardrobe.RevertAll();
                Svc.Chat.Print("[Glam Roulette] Put everyone back.");
                return;

            default:
                Svc.Chat.Print($"[Glam Roulette] Unknown argument '{argument}'. Use reroll, revert, on or off.");
                return;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (!EzThrottler.Throttle(UpdateThrottleName, 1000))
                return;

            wardrobe.Update();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
