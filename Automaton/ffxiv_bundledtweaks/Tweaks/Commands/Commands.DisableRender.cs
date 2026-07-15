using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace ComplexTweaks.Tweaks;

public partial class CommandsConfiguration {
    [BoolConfig(Label = "/norender")]
    public bool DisableRender = false;
}

public partial class Commands : Tweak<CommandsConfiguration> {
    [CommandHandler("/norender", "Toggle 3D rendering", nameof(Config.DisableRender))]
    internal unsafe void OnCommmandNoRender(string command, string arguments) => Manager.Instance()->Is3DRenderingDisabled ^= true;
}
