using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;

namespace SamplePlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly (XivChatType Type, string Label)[] Channels =
    [
        (XivChatType.CrossLinkShell1, "Cross-world Linkshell 1"),
        (XivChatType.CrossLinkShell2, "Cross-world Linkshell 2"),
        (XivChatType.CrossLinkShell3, "Cross-world Linkshell 3"),
        (XivChatType.CrossLinkShell4, "Cross-world Linkshell 4"),
        (XivChatType.CrossLinkShell5, "Cross-world Linkshell 5"),
        (XivChatType.CrossLinkShell6, "Cross-world Linkshell 6"),
        (XivChatType.CrossLinkShell7, "Cross-world Linkshell 7"),
        (XivChatType.CrossLinkShell8, "Cross-world Linkshell 8"),
        (XivChatType.Party, "Party"),
        (XivChatType.Say, "Say"),
        (XivChatType.Yell, "Yell"),
        (XivChatType.Shout, "Shout"),
        (XivChatType.FreeCompany, "Free Company"),
        (XivChatType.TellIncoming, "Whisper (Tell)"),
        (XivChatType.Echo, "Echo (Faloop / plugin messages)"),
    ];

    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("CWLS Go To Settings###CWLSGoToSettings")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(280, 380);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextWrapped("Choose which chat channels to watch for map links. Matching messages get a clickable [Go To] appended.");
        ImGui.Separator();

        foreach (var (type, label) in Channels)
        {
            var enabled = configuration.WatchedChannels.Contains(type);
            if (ImGui.Checkbox(label, ref enabled))
            {
                if (enabled)
                    configuration.WatchedChannels.Add(type);
                else
                    configuration.WatchedChannels.Remove(type);

                configuration.Save();
            }
        }

        ImGui.Separator();

        var autoOpen = configuration.AutoOpenHuntWindow;
        if (ImGui.Checkbox("Auto-open hunt tracker on new hunt", ref autoOpen))
        {
            configuration.AutoOpenHuntWindow = autoOpen;
            configuration.Save();
        }
        ImGui.TextDisabled("/hunts toggles the hunt tracker window.");

        ImGui.Separator();
        ImGui.TextUnformatted("Hunt relay message");
        var template = configuration.RelayMessageTemplate;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##relaytemplate", ref template, 200))
        {
            configuration.RelayMessageTemplate = template;
            configuration.Save();
        }

        ImGui.TextDisabled("Placeholders: {name} {world} {zone} {coords} and <flag>.\nThe map flag is set to the hunt's position before sending,\nso <flag> becomes a clickable location link.");
        if (ImGui.SmallButton("Reset to default"))
        {
            configuration.RelayMessageTemplate = Configuration.DefaultRelayTemplate;
            configuration.Save();
        }

        ImGui.Separator();

        var stopSnd = configuration.StopSndOnGoTo;
        if (ImGui.Checkbox("Stop automation on Go To", ref stopSnd))
        {
            configuration.StopSndOnGoTo = stopSnd;
            configuration.Save();
        }
        ImGui.TextDisabled("Hard-stops SND macros, the fate grinder and any active\nnavigation when a Go To starts from a link or the tracker.");
    }
}
