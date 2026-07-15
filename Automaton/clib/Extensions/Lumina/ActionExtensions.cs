using FFXIVClientStructs.FFXIV.Client.Game;
using Action = Lumina.Excel.Sheets.Action;

namespace clib.Extensions;

public static unsafe class ActionExtensions {
    extension(Action row) {
        public bool IsOnCooldown(ActionType type = ActionType.Action) {
            var group = GetRecastGroup(row);
            if (group is -1) return false;
            var recast = ActionManager.Instance()->GetRecastGroupDetail(group);
            return recast->Total - recast->Elapsed > 0;
        }
        public bool IsAvailable(ActionType type = ActionType.Action) => GetActionStatus(row, type) == 0 && !IsOnCooldown(row, type);

        public int GetRecastGroup(ActionType type = ActionType.Action) => ActionManager.Instance()->GetRecastGroup((int)type, row.RowId);
        public uint GetActionStatus(ActionType type = ActionType.Action) => ActionManager.Instance()->GetActionStatus(ActionType.Action, row.RowId);
    }
}
