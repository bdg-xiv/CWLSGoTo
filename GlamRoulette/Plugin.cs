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
    private const string PromptThrottleName = "GlamRoulettePrompt";

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
        config.Tidy();
        wardrobe.StampUnknownAsSeen();
        penumbra.OnRestart(OnPenumbraRestart);
        penumbra.OnCreating(OnCreatingCharacter);
        contextMenu = new PlayerContextMenu(config, wardrobe);
        window = new MainWindow(config, wardrobe, glamourer, dyes, penumbra, cplus, shoes);
        windows.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Svc.Framework.Update += OnFrameworkUpdate;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Glam Roulette. 'reroll' for everyone, 'me' for yourself, 'why' to log who is being skipped and why, 'redraw' to build everyone again, 'fix' to revert and re-deal everyone (clears black characters), 'off'/'on' to toggle.",
        });
    }

    /// <summary>Somebody's model is about to be built, and the wardrobe gets one chance to have
    /// their options baked into it. Only the address and the collection matter here - the other
    /// three are pointers offered for editing the build itself, which is not our business.</summary>
    private void OnCreatingCharacter(nint address, Guid collection, nint modelId, nint customize, nint equipData)
        => wardrobe.OnCreating(address, collection);

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
        penumbra.StopCreating(OnCreatingCharacter);

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
                Svc.Chat.Print(wardrobe.RerollMe() is { } outfit
                    ? "[Glam Roulette] Dealing yourself another one - outfit, colours, shoes, "
                      + $"mod options and body. The draw: {outfit}."
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

            case "redraw":
                Svc.Chat.Print($"[Glam Roulette] Building {wardrobe.RedrawEveryone()} character(s) again.");
                return;

            case "fix":
                Svc.Chat.Print($"[Glam Roulette] Putting {wardrobe.FixEveryone()} character(s) back first - "
                               + "fresh deals follow in a few seconds. This is the one that clears "
                               + "characters baked black.");
                return;

            case "why":
                // To the log rather than to chat: a crowded plaza is hundreds of lines, and a
                // list that long in chat is a list you cannot read.
                var lines = wardrobe.Explain();
                Svc.Log.Information($"[GlamRoulette] --- why, for {lines.Count} character(s) ---");
                foreach (var line in lines)
                    Svc.Log.Information($"[GlamRoulette] {line}");

                Svc.Chat.Print($"[Glam Roulette] Wrote {lines.Count} line(s) to the log - "
                               + "one per person in front of you, saying why.");
                return;

            default:
                Svc.Chat.Print($"[Glam Roulette] Unknown argument '{argument}'. "
                               + "Use reroll, revert, redraw, fix, why, on or off.");
                return;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // A prompt pass runs sooner than the scheduled one when a creation just left
            // somebody undressed, but never faster than a few a second - a zone-in creates a
            // hundred people in one burst, and "now" must not come to mean "every frame".
            if (!EzThrottler.Throttle(UpdateThrottleName, 1000)
                && !(wardrobe.WantsPrompt && EzThrottler.Throttle(PromptThrottleName, 150)))
                return;

            wardrobe.Update();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed during the framework update");
        }
    }
}
