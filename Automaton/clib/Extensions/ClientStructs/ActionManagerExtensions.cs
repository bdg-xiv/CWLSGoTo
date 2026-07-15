using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace clib.Extensions;

public static unsafe class ActionManagerExtensions {
    public record struct CastAction(ActionType Type, uint Id, float Elapsed, float Total);

    extension(ActionManager) {
        public static bool IsCasting(ActionType actionType, uint actionId) {
            if (Control.GetLocalPlayer() is not null and var player) {
                var info = player->GetCastInfo();
                return info is not null && info->IsCasting && info->ActionType == (byte)actionType && info->ActionId == actionId;
            }
            return false;
        }

        public static bool UseAction(ActionType actionType, uint actionId) {
            var am = ActionManager.Instance();
            return am is not null && am->UseAction(actionType, actionId);
        }

        public static bool IsActionInUse(ActionType type, uint itemId) {
            var am = ActionManager.Instance();
            return am is not null && am->GetActionStatus(type, itemId) != 0;
        }

        public static void ExecuteMainCommand(uint commandId) => UIModule.Instance()->ExecuteMainCommand(commandId);

        public static CastAction GetCastAction()
            => ActionManager.Instance() is not null and var am ? new CastAction(am->CastActionType, am->CastActionId, am->CastTimeElapsed, am->CastTimeTotal) : default;

        public static bool Teleport(uint aetheryteId, byte subIndex = 0) => UIState.Instance()->Telepo.Teleport(aetheryteId, subIndex);

        public static uint GetAdjustedSprintId() => GameMain.Instance()->CurrentTerritoryIntendedUseId switch {
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.CosmicExploration => 43357,
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.IslandSanctuary => 31314,
            _ => 4
        };

        public static uint GetAdjustedSprintStatusId() => GameMain.Instance()->CurrentTerritoryIntendedUseId switch {
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.CosmicExploration => 4398,
            FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.IslandSanctuary => 50,
            _ => 50
        };

        public static bool Sprint() => Control.GetLocalPlayer()->StatusManager.GetSprintTimeRemaining() switch {
            < 5 when ActionManager.GetAdjustedSprintId() is var id and not 4 => ActionManager.Instance()->UseAction(ActionType.Action, id),
            0 when ActionManager.GetAdjustedSprintId() is var id and 4 => ActionManager.Instance()->UseAction(ActionType.Action, id),
            _ => false
        };
    }
}
