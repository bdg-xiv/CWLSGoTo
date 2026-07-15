using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ComplexTweaks.Tweaks;

public record struct ClickToMoveSettings(bool Enabled, MovementType MovementType);

public class ClickToMoveConfiguration {
    public ClickToMoveSettings WorldClick = new() { Enabled = true };
    public ClickToMoveSettings MapClick = new() { Enabled = true };
    public ClickModifierKeys ClickModifier = ClickModifierKeys.Shift;
}

[Tweak]
public unsafe class ClickToMove : Tweak<ClickToMoveConfiguration> {
    public override string Name => "Click to Move";
    public override string Description => "Like those other games. Supports clicking on the map.";

    private OverrideMovement movement = null!;

    public override void Enable() {
        movement = new();
        Svc.Framework.Update += MoveTo;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "AreaMap", HandleMapClick);
    }

    public override void Disable() {
        movement.Dispose();
        Svc.Framework.Update -= MoveTo;
        Svc.AddonLifecycle.UnregisterListener(HandleMapClick);
    }

    public override void DrawConfig() {
        static void DrawProfile(string name, ref ClickToMoveSettings cfg) {
            using var id = ImRaii.PushId(name);
            ImGui.TextV(name);
            ImGui.SameLine();

            var worldEnabled = cfg.Enabled;
            if (ImGui.ToggleableCheckmark($"##{name}Enabled", ref worldEnabled)) {
                cfg = cfg with { Enabled = worldEnabled };
            }

            using var indent = ImRaii.PushIndent();
            ImGui.TextV("Movement Type");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(120);
            using var worldCombo = ImRaii.Combo($"##{name}MovementType", cfg.MovementType.ToString());
            if (worldCombo.Success) {
                foreach (var (typeName, value) in Enum.GetNames<MovementType>().Zip(Enum.GetValues<MovementType>())) {
                    if (ImGui.Selectable(typeName, cfg.MovementType == value)) {
                        cfg = cfg with { MovementType = value };
                    }
                    if (cfg.MovementType == value) {
                        ImGui.SetItemDefaultFocus();
                    }
                }
            }
        }

        ImGui.DrawSection("Configuration");
        DrawProfile("In-World Click", ref Config.WorldClick);
        DrawProfile("AreaMap Click", ref Config.MapClick);

        ImGui.TextV("Modifier Key");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        using (var modifierCombo = ImRaii.Combo($"###modifier", Config.ClickModifier.ToString().Replace("_", " "))) {
            if (modifierCombo) {
                foreach (var mod in Enum.GetValues<ClickModifierKeys>()) {
                    if (ImGui.Selectable(mod.ToString(), mod == Config.ClickModifier)) {
                        Config.ClickModifier = mod;
                    }
                }
            }
        }
        DrawCommands();
    }

    private void HandleMapClick(AddonEvent type, AddonArgs args) {
        if (!Config.MapClick.Enabled) return;
        if (args is AddonReceiveEventArgs { AtkEventType: AddonEventType.MouseDown } receiveArgs) {
            if (receiveArgs.AtkEventData.As<AtkEventData.AtkMouseData>()->ButtonId != 0) return; // left click only
            if (AgentMap.Instance()->CurrentMapId != AgentMap.Instance()->SelectedMapId) return;
            var success = Config.ClickModifier switch {
                ClickModifierKeys.None => true,
                ClickModifierKeys.Shift => receiveArgs.AtkEventData.As<AtkEventData>()->MouseData.Modifier.HasFlag(ModifierFlag.Shift),
                ClickModifierKeys.Ctrl => receiveArgs.AtkEventData.As<AtkEventData>()->MouseData.Modifier.HasFlag(ModifierFlag.Ctrl),
                ClickModifierKeys.Alt => receiveArgs.AtkEventData.As<AtkEventData>()->MouseData.Modifier.HasFlag(ModifierFlag.Alt),
                _ => false
            };
            if (!success) return;

            if (args.GetAddon<AddonAreaMap>()->GetMouseWorldCoords() is { } coords) {
                if (Config.MapClick.MovementType is MovementType.Pathfind)
                    Svc.Navmesh.PathfindAndMoveTo(coords.OnMesh(), Player.CanFly);
                else {
                    movement.Enabled = true;
                    movement.DesiredPosition = new(coords.X, Player.Position.Y, coords.Y);
                }
            }
        }
    }

    private bool wasPressed = false;
    private void MoveTo(IFramework framework) {
        if (!Config.WorldClick.Enabled) return;
        if (!Player.Available || Player.IsBusy) return;

        if (Config.WorldClick.MovementType != MovementType.Pathfind && Player.Object.FlatDistanceTo(movement.DesiredPosition) < 0.05f) {
            movement.Enabled = false;
        }

        var isPressed = ClickedInWorld();
        if (!wasPressed && isPressed)
            wasPressed = true;
        else if (wasPressed && !isPressed) {
            wasPressed = false;
            if (!Framework.Instance()->WindowInactive) {
                Svc.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out var pos, 100000f);
                if (Config.WorldClick.MovementType == MovementType.Pathfind) {
                    if (Service.Navmesh.IsRunning()) Service.Navmesh.Stop();
                    Service.Navmesh.PathfindAndMoveTo(pos, false);
                }
                else {
                    movement.Enabled = true;
                    movement.DesiredPosition = pos;
                }
            }
        }
    }

    private bool ClickedInWorld()
        => IsKeyPressed(ECommons.Interop.LimitedKeys.LeftMouseButton) && Utils.IsClickingInGameWorld() && Config.ClickModifier switch {
            ClickModifierKeys.None => true,
            ClickModifierKeys.Shift => ImGuiEx.Shift,
            ClickModifierKeys.Ctrl => ImGuiEx.Ctrl,
            ClickModifierKeys.Alt => ImGuiEx.Alt,
            _ => false
        };
}
