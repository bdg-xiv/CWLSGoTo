using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Lumina.Excel.Sheets;

namespace ComplexTweaks.Tweaks;

public class DebugToolsConfiguration {
    [BoolConfig] public bool AutoVoidIslandRest = false;
    [BoolConfig] public bool EnableTPClick = false;
    [BoolConfig] public bool EnableNoClip = false;

    [FloatConfig(DependsOn = nameof(EnableNoClip), DefaultValue = 0.05f)]
    public float NoClipSpeed = 0.05f;

    [BoolConfig] public bool EnableMoveSpeed = false;
    [BoolConfig] public bool EnableDirectActions = false;
    [BoolConfig] public bool EnableTPMarker = false;
    [BoolConfig] public bool EnableTPOffset = false;
    [BoolConfig] public bool EnableTPAbsolute = false;
}

[Tweak(true)]
public partial class DebugTools : Tweak<DebugToolsConfiguration> {
    public override string Name => "Debug Tools";
    public override string Description => "Debug tools for use in hyperborea/firewall";

    public override void Enable() {
        _keys = GetSheet<ConfigKey>().Where(x => x.RowId is >= 12 and <= 18).ToDictionary(x => x.Label.ToString(), x => x);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MJICraftSchedule", OnSetup);
        Svc.ClientState.EnterPvP += OnEnterPvP;
    }

    public override void Disable() {
        Svc.AddonLifecycle.UnregisterListener(OnSetup);
        Svc.ClientState.EnterPvP -= OnEnterPvP;
    }

    public override void OnConfigChange(string fieldName) {
        if (fieldName == nameof(Config.EnableTPClick) && !Config.EnableTPClick) {
            tpActive = false;
            ShowMouseOverlay = false;
        }

        if (fieldName == nameof(Config.EnableNoClip) && !Config.EnableNoClip)
            ncActive = false;
    }

    private unsafe void OnSetup(AddonEvent type, AddonArgs args) {
        if (!Config.AutoVoidIslandRest) return;
        if (AgentMJICraftSchedule.Instance()->Data->RestCycles.Hex() != 8321u) {
            Svc.Log.Debug($"Setting rest: {8321u:X}");
            AgentMJICraftSchedule.Instance()->Data->NewRestCycles = 8321u;
            var eventData = stackalloc int[] { 0, 0, 0 };
            var atkvalues = new Span<AtkValue>([new() { Type = AtkValueType.Int, Int = 0 }]);
            AgentMJICraftSchedule.Instance()->AgentInterface.ReceiveEvent((AtkValue*)eventData, atkvalues.GetPointer(0), (uint)atkvalues.Length, 5); // 5 = eventKind
        }
    }

    // prevent entering pvp with debug options enabled
    private void OnEnterPvP() {
        Player.Speed = 1.0f;
        tpActive = false;
        ncActive = false;
        ShowMouseOverlay = false;
    }

    public static bool ShowMouseOverlay;
    private bool IsLButtonPressed;
    private bool tpActive;
    private bool ncActive;
    private Dictionary<string, ConfigKey> _keys = null!;
}
