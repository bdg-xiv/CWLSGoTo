using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace LogoutOnTell;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/logoutontell";

    // Deliberately not persisted: the plugin is armed per session only, and logging in
    // always starts disarmed - otherwise it would still be armed right after it logged
    // you out and you came back.
    private bool enabled;

    private readonly TaskManager taskManager;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        // TaskManager's constructor hooks Svc.Framework.Update, so it must be created
        // after ECommonsMain.Init - a field initializer would run too early and throw.
        taskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = true });

        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.ClientState.Login += OnLogin;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles automatic logout when receiving a tell (resets to off every login). Also accepts: on, off, status."
        });
    }

    private void OnLogin()
    {
        if (!enabled)
            return;

        enabled = false;
        Svc.Log.Information("Disarmed on login");
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                enabled = true;
                break;
            case "off":
                enabled = false;
                break;
            case "status":
                PrintStatus();
                return;
            default:
                enabled = !enabled;
                break;
        }

        PrintStatus();
    }

    private void PrintStatus()
        => Svc.Chat.Print($"[LogoutOnTell] {(enabled ? "Enabled - you will be logged out when a tell arrives. Resets to off on next login." : "Disabled.")}");

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!enabled)
            return;

        if (message.LogKind != XivChatType.TellIncoming)
            return;

        // Skip tells another plugin already suppressed (e.g. spam filters blocking RMT
        // tells mark them handled) - the player never saw those, so don't log out.
        if (message.IsHandled)
            return;

        if (taskManager.IsBusy)
            return; // A logout is already underway.

        var sender = message.Sender.TextValue;
        Svc.Log.Information($"Tell received from {sender}, logging out");
        Svc.Chat.Print($"[LogoutOnTell] Tell received from {sender} - logging out.");

        // The game refuses /logout while occupied or in combat, so wait until it can
        // actually go through instead of firing blind.
        taskManager.Enqueue(WaitUntilLogoutPossible, "WaitUntilLogoutPossible", new TaskManagerConfiguration { TimeLimitMS = 60000, AbortOnTimeout = true });
        taskManager.Enqueue(() => Chat.SendMessage("/logout"), "SendLogout");
        // If Yes Already (or similar) confirms the dialog first, this just times out harmlessly.
        taskManager.Enqueue(ConfirmLogoutDialog, "ConfirmLogoutDialog", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false, TimeoutSilently = true });
    }

    private static bool? WaitUntilLogoutPossible()
        => Player.Available && Player.Interactable && !IsOccupied() && !Svc.Condition[ConditionFlag.InCombat];

    // We triggered /logout ourselves and only watch for the dialog within a short
    // window right after, so confirming the first SelectYesno that appears is safe.
    private static unsafe bool? ConfirmLogoutDialog()
    {
        if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) || !IsAddonReady(addon))
            return false;

        new AddonMaster.SelectYesno((nint)addon).Yes();
        return true;
    }

    public void Dispose()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.ClientState.Login -= OnLogin;
        Svc.Commands.RemoveHandler(CommandName);
        taskManager.Abort();
        taskManager.Dispose();
        ECommonsMain.Dispose();
    }
}
