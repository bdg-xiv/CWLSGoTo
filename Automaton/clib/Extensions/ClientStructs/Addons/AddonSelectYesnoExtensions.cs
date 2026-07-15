using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace clib.Extensions;

public static unsafe class AddonSelectYesnoExtensions {
    extension(AddonSelectYesno) {
        public static void Yes() {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
            if (addon != null && addon->IsReady) {
                var evt = new AtkEvent() { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
                var data = new AtkEventData();
                addon->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt, &data);
            }
        }

        public static void No() {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
            if (addon != null && addon->IsReady) {
                var evt = new AtkEvent() { Listener = &addon->AtkEventListener, Target = &AtkStage.Instance()->AtkEventTarget };
                var data = new AtkEventData();
                addon->ReceiveEvent(AtkEventType.ButtonClick, 1, &evt, &data);
            }
        }
    }
}
