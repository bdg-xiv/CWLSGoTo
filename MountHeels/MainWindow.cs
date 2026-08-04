using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace MountHeels;

internal sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly MountWatcher watcher;
    private readonly SimpleHeelsIpc heels;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.45f, 1f, 0.5f, 1f);

    public MainWindow(Configuration config, MountWatcher watcher, SimpleHeelsIpc heels)
        : base("Mount Heels###MountHeels")
    {
        this.config = config;
        this.watcher = watcher;
        this.heels = heels;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 240),
            MaximumSize = new Vector2(800, 900),
        };
    }

    private static string NameOf(uint mountId)
    {
        var sheet = Svc.Data.GetExcelSheet<Mount>();
        if (sheet != null && sheet.TryGetRow(mountId, out var mount))
        {
            var name = mount.Singular.ExtractText();
            if (name.Length > 0)
                return char.ToUpperInvariant(name[0]) + name[1..];
        }

        return $"Mount #{mountId}";
    }

    public override void Draw()
    {
        if (!heels.Available)
        {
            ImGui.TextColored(Bad, "Simple Heels is not responding - nothing can be adjusted.");
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
            if (!enabled)
                watcher.Release();
        }

        ImGui.Separator();

        if (watcher.Mounted)
        {
            ImGui.Text($"Riding: {NameOf(watcher.CurrentMount)}");
            ImGui.SameLine();
            ImGui.TextColored(watcher.Pushing ? Good : Dim, watcher.Pushing ? "(adjusted)" : "(left alone)");
            DrawEditor(watcher.CurrentMount);
        }
        else
        {
            ImGui.TextColored(Dim, "Not mounted. Summon the mount you want to adjust and it will appear here.");
        }

        ImGui.Separator();
        ImGui.Text($"{config.Offsets.Count} mount{(config.Offsets.Count == 1 ? "" : "s")} adjusted");

        foreach (var (mountId, offset) in config.Offsets.OrderBy(o => NameOf(o.Key)).ToList())
        {
            ImGui.PushID((int)mountId);
            ImGui.Text($"  {NameOf(mountId)}");
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"Y {offset.Y:F3}" +
                                   (offset.X != 0 || offset.Z != 0 ? $"  X {offset.X:F3}  Z {offset.Z:F3}" : "") +
                                   (offset.Rotation != 0 ? $"  R {offset.Rotation:F2}" : ""));
            ImGui.SameLine();
            if (ImGui.SmallButton("Forget"))
            {
                config.Offsets.Remove(mountId);
                config.Save();
                watcher.Refresh();
            }
            ImGui.PopID();
        }
    }

    private void DrawEditor(uint mountId)
    {
        if (!config.Offsets.TryGetValue(mountId, out var offset))
        {
            if (ImGui.Button("Adjust this mount"))
            {
                config.Offsets[mountId] = new MountOffset();
                config.Save();
                watcher.Refresh();
            }

            ImGui.SameLine();
            ImGui.TextColored(Dim, "Simple Heels' own Mounted offset is being used.");
            return;
        }

        ImGui.Indent();

        var y = offset.Y;
        if (ImGui.DragFloat("Height", ref y, 0.001f, -2f, 2f, "%.3f"))
            Set(mountId, offset with { Y = y });

        if (ImGui.TreeNode("Sideways, forward and turn"))
        {
            var x = offset.X;
            if (ImGui.DragFloat("X", ref x, 0.001f, -2f, 2f, "%.3f"))
                Set(mountId, offset with { X = x });

            var z = offset.Z;
            if (ImGui.DragFloat("Z", ref z, 0.001f, -2f, 2f, "%.3f"))
                Set(mountId, offset with { Z = z });

            var r = offset.Rotation;
            if (ImGui.DragFloat("Rotation", ref r, 0.01f, -3.15f, 3.15f, "%.2f"))
                Set(mountId, offset with { Rotation = r });

            ImGui.TreePop();
        }

        if (ImGui.Button("Reset to zero"))
            Set(mountId, new MountOffset());

        ImGui.SameLine();
        if (ImGui.Button("Stop adjusting this mount"))
        {
            config.Offsets.Remove(mountId);
            config.Save();
            watcher.Refresh();
        }

        ImGui.TextColored(Dim, "An offset of all zeroes counts as \"leave it alone\".");
        ImGui.Unindent();
    }

    private void Set(uint mountId, MountOffset offset)
    {
        config.Offsets[mountId] = offset;
        config.Save();
        // Force the next tick to push again so the change is visible while dragging.
        watcher.Refresh();
    }
}
