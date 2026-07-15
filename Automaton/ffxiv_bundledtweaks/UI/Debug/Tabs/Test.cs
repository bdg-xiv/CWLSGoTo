using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.Exd;

namespace ComplexTweaks.UI.Debug.Tabs;

internal unsafe class TestTab : DebugTab {
    public override void Draw() {
        ImGui.Text($"{Svc.Interface.InternalName}: {Svc.Interface.GetPluginConfigDirectory()}");

        if (ImGui.Button($"compress"))
            ImGui.SetClipboardText(ImGui.GetClipboardText().ToBase64());
        if (ImGui.Button($"decompress"))
            ImGui.SetClipboardText(ImGui.GetClipboardText().FromBase64());
        if (ImGui.Button("logout"))
            AgentLobby.Instance()->HandleLogout(false, 60);

        if (ImGui.Button("leave"))
            EventFramework.LeaveCurrentContent(true);

        if (ImGui.Button("meld"))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 12);

        ImGui.Text($"{InfoProxyNoviceNetwork.IsInNoviceNetwork()}");

        if (Svc.Targets.Target != null)
            ImGui.Text($"In Interact Range: {Svc.Targets.Target.IsInInteractRange()}");

        ImGui.Text($"{ExdModule.GetRoleForClassJobId(Player.ClassJob.RowId)}");

        ImGui.Text($"Target LoS: {Svc.Targets.Target?.IsInLineOfSight(Player.Position) ?? false}");
    }
}
