using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Numerics;

namespace LetGo;

/// <summary>
/// Clears the target while a modifier is held and hands it back on release.
///
/// The key state is read from ImGui's IO rather than Dalamud's key state: modifiers are
/// not guaranteed to be in the game's own key table, and ImGui is fed straight from the
/// window's messages, so it also stops reporting the key as held the moment the game loses
/// focus - which is exactly when nothing should happen.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/letgo";

    private readonly Configuration config;

    private bool held;

    /// <summary>Object id of the target taken away, or 0 when there is nothing owed back.
    /// Stored by id rather than as the object itself so a mob that despawns mid-hold
    /// cannot be handed back as a stale reference.</summary>
    private ulong owed;

    private bool windowOpen;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => windowOpen = !windowOpen)
        {
            HelpMessage = "Settings for dropping your target while a key is held.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= Draw;

        Svc.Commands.RemoveHandler(CommandName);

        // Leaving mid-hold shouldn't cost the target.
        Restore();

        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => windowOpen = !windowOpen;

    private void Draw()
    {
        Tick();
        DrawWindow();
    }

    private void Tick()
    {
        if (!config.Enabled || !Svc.ClientState.IsLoggedIn)
        {
            // Don't strand a target if the plugin is switched off mid-hold.
            if (held) Restore();
            held = false;
            return;
        }

        var io = ImGui.GetIO();

        // Typing a capital letter must not flick the target away.
        var typing = io.WantTextInput || GameTextInputActive();

        var down = !typing && config.Key switch
        {
            Modifier.Ctrl => io.KeyCtrl,
            Modifier.Alt => io.KeyAlt,
            _ => io.KeyShift,
        };

        if (down == held)
            return;

        held = down;

        if (held)
            Drop();
        else
            Restore();
    }

    private void Drop()
    {
        // Only on the press: re-clearing every frame would fight the player if they
        // deliberately target something while the key is down.
        var target = Svc.Targets.Target;
        if (target == null)
            return;

        owed = target.GameObjectId;
        Svc.Targets.Target = null;
    }

    private void Restore()
    {
        var id = owed;
        owed = 0;

        if (id == 0)
            return;

        // Anything picked up while the key was held is the newer intent - leave it.
        if (Svc.Targets.Target != null)
            return;

        var target = Svc.Objects.SearchById(id);
        if (target != null)
            Svc.Targets.Target = target;
    }

    private static unsafe bool GameTextInputActive()
    {
        var module = RaptureAtkModule.Instance();
        return module != null && module->AtkModule.IsTextInputActive();
    }

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(420, 210), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Let Go###LetGo", ref windowOpen))
        {
            if (ImGui.Checkbox("Enabled", ref config.Enabled))
                config.Save();

            var key = (int)config.Key;
            if (ImGui.Combo("Key", ref key, "Shift\0Ctrl\0Alt\0"))
            {
                config.Key = (Modifier)key;
                config.Save();
            }

            ImGui.Separator();
            ImGui.TextWrapped("Hold the key to clear your target; release it to get the same "
                + "target back. Target something else while the key is down and that choice "
                + "is kept instead.");

            if (config.Key == Modifier.Shift)
            {
                ImGui.Spacing();
                ImGui.TextWrapped("Shift is also a common hotbar modifier. If yours is, switch "
                    + "this to Ctrl or Alt - otherwise a shift-modified action fires with no target.");
            }
        }

        ImGui.End();
    }
}
