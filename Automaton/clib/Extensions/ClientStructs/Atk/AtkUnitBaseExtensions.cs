using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace clib.Extensions;

public static unsafe class AtkUnitBaseExtensions {
    extension(AtkUnitBase) {
        public static bool IsAddonReady(string name) {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
            return addon != null && addon->IsVisible && addon->IsReady && addon->IsFullyLoaded();
        }

        public static bool CloseAddon(string name) {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
            if (addon == null || !addon->IsVisible)
                return false;
            addon->Close(false);
            return true;
        }
    }

    public static T* GetNodeById<T>(this ref AtkUnitBase addon, uint nodeId) where T : unmanaged
       => addon.UldManager.SearchNodeById<T>(nodeId);
}
