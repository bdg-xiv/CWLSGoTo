using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Lumina.Excel.Sheets;
using System.Text;

namespace clib.Extensions;

public static class IChatGuiExtensions {
    public static unsafe void ExecuteCommand(this IChatGui _, string command) {
        if (!command.StartsWith('/'))
            return;
        using var cmd = new Utf8String(command);
        RaptureShellModule.Instance()->ExecuteCommandInner(&cmd, UIModule.Instance());
    }

    public static unsafe void SendMessageUnsafe(this IChatGui _, byte[] message) {
        var mes = Utf8String.FromSequence(message.NullTerminate());
        UIModule.Instance()->ProcessChatBoxEntry(mes);
        mes->Dtor(true);
    }

    public static void SendMessage(this IChatGui _, string message) {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
            throw new ArgumentException("message is empty", nameof(message));

        if (bytes.Length > 500)
            throw new ArgumentException("message is too long", nameof(message));

        if (message.Length != SanitiseText(message).Length)
            throw new ArgumentException("message contains invalid characters", nameof(message));

        SendMessageUnsafe(_, bytes);
    }

    public static void PrintMessage(this IChatGui chat, string message)
        => chat.Print(new XivChatEntry {
            Type = XivChatType.Echo,
            Message = $"[{CLibMain.Name}] {message}"
        });

    public static void PrintError(this IChatGui chat, string message)
        => chat.Print(new XivChatEntry {
            Type = XivChatType.SystemError,
            Message = $"[{CLibMain.Name}] {message}"
        });

    public static void PrintColor(this IChatGui chat, string message, UIColor color)
        => chat.Print(new XivChatEntry {
            Type = XivChatType.Echo,
            Message = new SeString(
                new UIForegroundPayload((ushort)color.RowId),
                new TextPayload($"[{CLibMain.Name}] {message}"),
                UIForegroundPayload.UIForegroundOff)
        });

    private static unsafe string SanitiseText(string text) {
        var uText = Utf8String.FromString(text);

        uText->SanitizeString((AllowedEntities)0x27F);
        var sanitised = uText->ToString();
        uText->Dtor(true);

        return sanitised;
    }
}
