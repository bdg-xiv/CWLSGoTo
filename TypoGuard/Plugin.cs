using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using static ECommons.GenericHelpers;

namespace TypoGuard;

public sealed unsafe class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/typoguard";
    private static readonly TimeSpan ForceResendWindow = TimeSpan.FromSeconds(5);

    public Configuration Configuration { get; }

    private readonly Hook<UIModule.Delegates.ProcessChatBoxEntry> processChatBoxEntryHook;

    private string? lastBlockedText;
    private DateTime lastBlockedAt = DateTime.MinValue;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // ProcessChatBoxEntry handles everything typed into the chat box, before the
        // game decides whether it's a command or a message - the right place to catch
        // "7artisan" before it goes to chat.
        processChatBoxEntryHook = Svc.Hook.HookFromAddress<UIModule.Delegates.ProcessChatBoxEntry>(
            (nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry, ProcessChatBoxEntryDetour);
        processChatBoxEntryHook.Enable();

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggles blocking of chat messages that start with 7 (mistyped / on LATAM layouts). Also accepts: on, off, status."
        });
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                Configuration.Enabled = true;
                break;
            case "off":
                Configuration.Enabled = false;
                break;
            case "status":
                PrintStatus();
                return;
            default:
                Configuration.Enabled = !Configuration.Enabled;
                break;
        }

        Configuration.Save();
        PrintStatus();
    }

    private void PrintStatus()
        => Svc.Chat.Print($"[TypoGuard] {(Configuration.Enabled ? "Enabled - messages starting with 7 + letter are blocked." : "Disabled.")}");

    private void ProcessChatBoxEntryDetour(UIModule* uiModule, Utf8String* message, nint a4, bool saveToHistory)
    {
        try
        {
            if (Configuration.Enabled && message != null)
            {
                var text = ReadSeString(message).GetText();
                if (LooksLikeMistypedCommand(text) && !IsForcedResend(text))
                {
                    lastBlockedText = text;
                    lastBlockedAt = DateTime.UtcNow;

                    var preview = text.Length > 40 ? text[..40] + "..." : text;
                    Svc.Log.Information($"Blocked mistyped command: {text}");
                    Svc.Chat.PrintError($"[TypoGuard] Blocked \"{preview}\" - looks like a mistyped command (7 instead of /). Send it again within 5 seconds if you really meant it.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"TypoGuard check failed, letting the message through: {ex.Message}");
        }

        processChatBoxEntryHook.Original(uiModule, message, a4, saveToHistory);
    }

    // Slash commands always start with a letter after the slash, so "7" + letter is
    // the mistyped-command shape ("7artisan"). "777" or "7 pm" pass through.
    private static bool LooksLikeMistypedCommand(string text)
        => text.Length >= 2 && text[0] == '7' && char.IsLetter(text[1]);

    private bool IsForcedResend(string text)
    {
        if (lastBlockedText != text || DateTime.UtcNow - lastBlockedAt > ForceResendWindow)
            return false;

        lastBlockedText = null;
        return true;
    }

    public void Dispose()
    {
        processChatBoxEntryHook.Disable();
        processChatBoxEntryHook.Dispose();
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }
}
