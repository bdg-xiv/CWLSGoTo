using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace clib.Extensions;

public static class StatusListExtensions {
    private static readonly uint[] TwistOfFateStatusIDs = [1288, 1289];

    extension(StatusList list) {
        public bool HasTwistOfFate() => list.Any(s => TwistOfFateStatusIDs.Contains(s.StatusId));
        public bool IsSprinting() => list.Any(s => ActionManager.GetAdjustedSprintStatusId() == s.StatusId);
        public float GetSprintTimeRemaining() => list.FirstOrDefault(s => ActionManager.GetAdjustedSprintStatusId() == s.StatusId)?.RemainingTime ?? 0;
    }
}
