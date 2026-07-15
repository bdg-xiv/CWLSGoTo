using FFXIVClientStructs.FFXIV.Client.Game;

namespace clib.Extensions;

public static class StatusManagerExtensions {
    extension(StatusManager mgr) {
        public bool IsSprinting() => mgr.HasStatus(ActionManager.GetAdjustedSprintStatusId());
        public float GetSprintTimeRemaining() => mgr.GetRemainingTime(mgr.GetStatusIndex(ActionManager.GetAdjustedSprintStatusId()));
    }
}
