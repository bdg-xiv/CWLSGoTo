using System;
using Dalamud.Bindings.ImGui;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.DalamudServices;

namespace DesignFromMod;

/// <summary>
/// A button inside Penumbra's own mod panel that makes a Glamourer design out of whatever the
/// mod changes. Penumbra hands out the mod being drawn on every frame of its settings panel,
/// so there is no guessing at what "the selected mod" is and no second window to keep in sync
/// with the first.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly Bridge bridge = new();
    private readonly ICallGateSubscriber<string, object?> postSettingsDraw;
    private readonly Action<string> handler;

    /// <summary>The mod the last click was about, and what came of it - Penumbra redraws this
    /// panel every frame, so the answer has to be kept rather than shown once.</summary>
    private string reportFor = string.Empty;
    private string report = string.Empty;
    private bool worked;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);

        handler = Draw;
        postSettingsDraw = Svc.PluginInterface.GetIpcSubscriber<string, object?>("Penumbra.PostSettingsDraw");
        postSettingsDraw.Subscribe(handler);
    }

    public void Dispose()
    {
        try
        {
            postSettingsDraw.Unsubscribe(handler);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[DesignFromMod] Could not let go of Penumbra's panel: {ex.Message}");
        }

        ECommonsMain.Dispose();
    }

    /// <summary>
    /// Drawn by Penumbra, inside its own window, with the mod it is showing. Anything thrown
    /// here would be thrown inside Penumbra's draw, so nothing is allowed out.
    /// </summary>
    private void Draw(string modDirectory)
    {
        try
        {
            if (modDirectory.Length == 0 || !bridge.GlamourerReady)
                return;

            if (modDirectory != reportFor)
            {
                report = string.Empty;
                reportFor = modDirectory;
            }

            var wearable = bridge.Wearable(modDirectory);
            if (wearable.Count == 0)
                return;

            ImGui.Separator();

            var name = bridge.NameOf(modDirectory);
            if (ImGui.Button($"Make a Glamourer design from these {wearable.Count} item(s)"))
                (worked, report) = bridge.Create(modDirectory, name);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Creates a design called \"{name}\" wearing what this mod changes.\n" +
                                 "Equipment only - it will not touch anyone's face or colouring.\n" +
                                 "Nothing about the mod itself is changed.");

            if (report.Length > 0)
                ImGui.TextColored(worked ? new System.Numerics.Vector4(0.45f, 1f, 0.5f, 1f)
                    : new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), report);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed drawing inside Penumbra");
        }
    }
}
