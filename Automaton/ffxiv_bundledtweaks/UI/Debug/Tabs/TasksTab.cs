using ComplexTweaks.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Threading.Tasks;

namespace ComplexTweaks.UI.Debug.Tabs;

internal class TasksTab : DebugTab {
    public override void Draw() {
        using (ImRaii.Disabled(!Svc.Automation.Running))
            if (ImGui.Button("Stop current task"))
                Svc.Automation.Stop();
        ImGui.Text($"{Svc.Automation.Name}: {Svc.Automation.Status}");

        if (ImGui.Button("transmute"))
            Svc.Automation.Start(new MateriaTransmutation());

        if (ImGui.Button("void all weeaboos"))
            Svc.Automation.Start(new VoidMatches("weeaboo"));

        if (ImGui.Button($"{nameof(MoveNonGearsetFromArmoury)}"))
            Svc.Automation.Start(new MoveNonGearsetFromArmoury());
    }

    private class VoidMatches(string name) : TaskBase {
        protected override async Task Execute() {
            foreach (var obj in Svc.Objects.OfType<IBattleChara>().Where(o => o.Name.TextValue.Contains(name, StringComparison.InvariantCultureIgnoreCase))) {
                Svc.Targets.Target = obj;
                Svc.Chat.SendMessage("/voidtarget");
                await NextFrame();
            }
        }
    }

    private class MoveNonGearsetFromArmoury : TaskBase {
        protected override async Task Execute() {
            foreach (var cont in InventoryType.Armoury) {
                foreach (var item in cont.Items) {
                    if (item is { ItemId: not 0, InGearset: false }) {
                        await WaitUntil(Svc.Condition.CanMoveItems, "WaitForPermission");
                        item.MoveTo(InventoryType.Bags);
                    }
                }
            }
        }
    }
}
