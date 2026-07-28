using Dalamud.Game.Command;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ArtisanGatherBridge;

/// <summary>Borrows /gather and /gatherfish from GatherBuddy Reborn.
///
/// Artisan's "Gather Item" does nothing more than type /gather &lt;item&gt; into the chat box,
/// so that is the only place the request can be caught - and GatherBuddy Reborn already
/// owns both commands. They are taken over here and handed straight back whenever the
/// request cannot be traced to an Artisan crafting list, which is every time they are
/// typed by hand, so nothing about using them normally changes.
///
/// Dalamud hands plugins a scoped command manager that refuses to unregister another
/// plugin's commands, hence the reflection onto the real one behind it.</summary>
internal sealed class CommandRelay : IDisposable
{
    internal const string GatherCommand = "/gather";
    internal const string FishCommand = "/gatherfish";

    private const string GatherBuddyReborn = "GatherBuddyReborn";

    /// <summary>Handles the request; false means "not ours, give it back".</summary>
    private readonly Func<string, string, bool> route;

    private readonly object? commandManager;
    private readonly Dictionary<string, CommandInfo> claimed = [];
    private readonly Dictionary<string, IReadOnlyCommandInfo> displaced = [];

    public CommandRelay(Func<string, string, bool> route)
    {
        this.route = route;
        commandManager = Svc.Commands.GetFoP("commandManagerService");
        if (commandManager == null)
            Svc.Log.Error("Couldn't reach Dalamud's command manager; /gather can't be intercepted.");

        Claim();
        DalamudReflector.RegisterOnInstalledPluginsChangedEvents(OnPluginsChanged);
    }

    public void Dispose()
    {
        foreach (var command in claimed.Keys)
            Svc.Commands.RemoveHandler(command);
        claimed.Clear();

        // Handing back a delegate that points into an unloaded plugin would be worse than
        // leaving the command unregistered.
        if (GatherBuddyRebornLoaded)
            foreach (var (command, original) in displaced)
                Restore(command, original);
        displaced.Clear();
    }

    private static bool GatherBuddyRebornLoaded
        => DalamudReflector.TryGetDalamudPlugin(GatherBuddyReborn, out _, suppressErrors: true, ignoreCache: true);

    private void OnPluginsChanged()
    {
        // GatherBuddy Reborn going away takes its handlers with it.
        if (!GatherBuddyRebornLoaded)
            displaced.Clear();

        Claim();
    }

    private void Claim()
    {
        foreach (var command in (string[])[GatherCommand, FishCommand])
        {
            var current = Svc.Commands.Commands.GetValueOrDefault(command);

            if (claimed.TryGetValue(command, out var mine))
            {
                // Still ours, nothing to do. If it isn't, something else took it, and
                // Dalamud's scoped command manager offers no clean way to re-register.
                if (!ReferenceEquals(current, mine))
                    Svc.Log.Warning($"{command} is no longer registered by this plugin.");
                continue;
            }

            if (current != null)
            {
                if (!RemoveExisting(command))
                {
                    Svc.Log.Warning($"Couldn't take {command} over from its current owner.");
                    continue;
                }

                displaced[command] = current;
            }

            var info = new CommandInfo((cmd, args) => Dispatch(cmd, args))
            {
                HelpMessage = "Files the item into a GatherBuddy Reborn list, or passes it on.",
                ShowInHelp = false,
            };

            if (Svc.Commands.AddHandler(command, info))
                claimed[command] = info;
            else
                Svc.Log.Error($"Failed to register {command}.");
        }
    }

    private void Dispatch(string command, string arguments)
    {
        try
        {
            if (route(command, arguments.Trim()))
                return;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to handle {command}; passing it on.");
        }

        Fallback(command, arguments);
    }

    private void Fallback(string command, string arguments)
    {
        if (displaced.TryGetValue(command, out var original))
        {
            original.Handler(command, arguments);
            return;
        }

        // Nothing was displaced, which means this plugin got to the command first and
        // GatherBuddy Reborn's own registration failed. Its handler is still there as a
        // method, so call that instead.
        if (DalamudReflector.TryGetDalamudPlugin(GatherBuddyReborn, out var plugin, suppressErrors: true, ignoreCache: true))
        {
            var name = command == FishCommand ? "OnGatherFish" : "OnGather";
            var method = plugin.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(plugin, [command, arguments]);
                return;
            }
        }

        Svc.Chat.PrintError($"[Artisan GBR Bridge] {command} couldn't be passed on to GatherBuddy Reborn.");
    }

    private bool RemoveExisting(string command)
        => commandManager?.GetType()
            .GetMethod("RemoveHandler", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(commandManager, [command]) is true;

    private void Restore(string command, IReadOnlyCommandInfo original)
    {
        if (original is not CommandInfo info)
            return;

        // The three argument overload also restores the "which plugin owns this command"
        // bookkeeping the installer shows.
        var method = commandManager?.GetType().GetMethod(
            "AddHandler",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(string), typeof(CommandInfo), typeof(string)],
            null);

        if (method != null)
            method.Invoke(commandManager, [command, info, GatherBuddyReborn]);
        else
            Svc.Log.Warning($"Couldn't hand {command} back to GatherBuddy Reborn.");
    }
}
