using clib.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static unsafe class IPlayerCharacterExtensions {
    extension(IPlayerCharacter? pc) {
        public bool Available => pc != null;
        public bool Interactable => pc?.IsTargetable ?? false;
        public bool IsMoving => get_Available(pc) && (AgentMap.Instance()->IsPlayerMoving || get_IsJumping(pc));
        public bool IsJumping => get_Available(pc) && (Svc.Condition[ConditionFlag.Jumping] || Svc.Condition[ConditionFlag.Jumping61] || pc?.Character->IsJumping());
        public bool IsAirDismountable {
            get {
                var ground = new FFXIVClientStructs.FFXIV.Common.Math.Vector3();
                return UIState.Instance()->GetIsAirDismountable(&ground);
            }
        }

        public bool IsBusy
            => Svc.Condition.IsUnavailable() ||
            !get_Interactable(pc) ||
            (pc?.IsCasting ?? false) ||
            get_IsMoving(pc) ||
            ActionManager.Instance()->AnimationLock > 0 ||
            Svc.Condition[ConditionFlag.InCombat] ||
            !GameMain.IsTerritoryLoaded;

        public bool IsUiFading => RaptureAtkUnitManager.Instance() is not null and var mgr && mgr->IsUiFading;

        public RowRef<TerritoryType> Territory => TerritoryType.GetRowRef(Svc.ClientState.TerritoryType);

        public bool CanMount => pc.Territory.Value.Mount && PlayerState.Instance()->NumOwnedMounts > 0;
        public bool Mounted => Svc.Condition[ConditionFlag.Mounted];
        public bool InFlight => Svc.Condition[ConditionFlag.InFlight];
        public float Rotation {
            get => pc?.Character->Rotation;
            set => pc?.Character->SetRotation(value);
        }
        /// <summary>
        /// Rotation packed into a ushort. Used in some <see cref="GameMain.ExecuteCommand"/> functions.
        /// </summary>
        public float PackedRotation => (ushort)(((Svc.Objects.LocalPlayer?.Rotation + Math.PI) / (2 * Math.PI) * 65536) ?? 0);

        public bool HasChocoboStabled => PlayerState.Instance()->IsPlayerStateFlagSet(PlayerStateFlag.IsBuddyInStable);
    }
}
