using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using ECommons.Interop;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace ComplexTweaks.Tweaks;

public partial class DebugTools : Tweak<DebugToolsConfiguration> {
    [FrameworkUpdate(nameof(Config.EnableTPClick))]
    private unsafe void OnTeleportClickUpdate(IFramework framework) {
        if (!Player.Available || IsOccupied()) return;

        ShowMouseOverlay = false;
        if (!tpActive)
            return;

        if (!Framework.Instance()->WindowInactive && IsKeyPressed([LimitedKeys.LeftControlKey, LimitedKeys.RightControlKey]) && Utils.IsClickingInGameWorld()) {
            ShowMouseOverlay = true;
            var pos = ImGui.GetMousePos();
            if (Svc.GameGui.ScreenToWorld(pos, out var res)) {
                if (IsKeyPressed(LimitedKeys.LeftMouseButton)) {
                    if (!IsLButtonPressed)
                        Player.SetPosition(res);
                    IsLButtonPressed = true;
                }
                else
                    IsLButtonPressed = false;
            }
        }
    }

    [FrameworkUpdate(nameof(Config.EnableNoClip))]
    private unsafe void OnNoClipUpdate(IFramework framework) {
        if (!Player.Available || IsOccupied()) return;
        if (!ncActive || Framework.Instance()->WindowInactive)
            return;

        var cx = Player.Position.X;
        var cy = Player.Position.Z;
        var angle = MathF.PI - CameraManager.Instance()->GetActiveCamera()->DirH;
        if (_keys["JUMP"].IsHeldRaw())
            Player.SetPosition((Player.Position.X, Player.Position.Y + Config.NoClipSpeed, Player.Position.Z).ToVector3());
        if (Svc.KeyState.GetRawValue(VirtualKey.LSHIFT) != 0 || IsKeyPressed(LimitedKeys.LeftShiftKey))
            Player.SetPosition((Player.Position.X, Player.Position.Y - Config.NoClipSpeed, Player.Position.Z).ToVector3());
        if (_keys["MOVE_FORE"].IsHeldRaw())
            Player.SetPosition(Player.Position.AddZ(Config.NoClipSpeed).RotatePoint(cx, cy, angle));
        if (_keys["MOVE_BACK"].IsHeldRaw())
            Player.SetPosition(Player.Position.AddZ(-Config.NoClipSpeed).RotatePoint(cx, cy, angle));
        if (_keys["MOVE_LEFT"].IsHeldRaw() || _keys["MOVE_STRIFE_L"].IsHeldRaw())
            Player.SetPosition(Player.Position.AddX(Config.NoClipSpeed).RotatePoint(cx, cy, angle));
        if (_keys["MOVE_RIGHT"].IsHeldRaw() || _keys["MOVE_STRIFE_R"].IsHeldRaw())
            Player.SetPosition(Player.Position.AddX(-Config.NoClipSpeed).RotatePoint(cx, cy, angle));
    }
}

