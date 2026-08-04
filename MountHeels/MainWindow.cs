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
    private readonly SimpleHeelsLink heels;

    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Good = new(0.45f, 1f, 0.5f, 1f);

    public MainWindow(Configuration config, MountWatcher watcher, SimpleHeelsLink heels)
        : base("Mount Heels###MountHeels")
    {
        this.config = config;
        this.watcher = watcher;
        this.heels = heels;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 260),
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

        var height = watcher.StandingHeight;
        if (height == 0f)
            ImGui.TextColored(Bad, "Your shoes are not giving Simple Heels a height right now.");
        else
            ImGui.TextColored(Dim, $"Your shoes are worth {height:F4} on the ground.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Whatever Simple Heels already lifts you by while standing, whether that\n" +
                             "came out of the mod or was set by hand for those shoes. Mounts below\n" +
                             "follow it, so changing shoes changes them too.");

        ImGui.Separator();

        if (watcher.Mounted && watcher.CurrentMount != 0)
        {
            ImGui.Text($"Riding: {NameOf(watcher.CurrentMount)}");
            ImGui.SameLine();
            ImGui.TextColored(watcher.Adjusting ? Good : Dim, watcher.Adjusting ? "(adjusted)" : "(left alone)");
            DrawEditor(watcher.CurrentMount);
        }
        else if (watcher.Mounted)
        {
            ImGui.TextColored(Dim, "Riding behind someone else - their mount, their offset.");
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
            ImGui.TextColored(Dim, Describe(offset));
            ImGui.SameLine();
            if (ImGui.SmallButton("Forget"))
            {
                config.Offsets.Remove(mountId);
                config.Save();
                watcher.Release();
            }
            ImGui.PopID();
        }
    }

    private static string Describe(MountOffset offset)
    {
        var height = offset.UseModelHeight
            ? offset.Y == 0f ? "shoes" : $"shoes {offset.Y:+0.###;-0.###}"
            : $"{offset.Y:F3}";

        return height +
               (offset.X != 0 || offset.Z != 0 ? $"  X {offset.X:F3}  Z {offset.Z:F3}" : "") +
               (offset.Rotation != 0 ? $"  {offset.Rotation:F0}°" : "");
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

        var useModel = offset.UseModelHeight;
        if (ImGui.Checkbox("Stand at the height the shoes are giving", ref useModel))
            Set(mountId, offset with { UseModelHeight = useModel });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For a mount you stand on top of, this is the same lift you get on the\n" +
                             "ground - which is exactly what is missing while riding one.");

        var y = offset.Y;
        if (ImGui.DragFloat(useModel ? "Extra height" : "Height", ref y, 0.001f, -2f, 2f, "%.3f"))
            Set(mountId, offset with { Y = y });

        if (useModel)
            ImGui.TextColored(Dim, $"    Standing at {watcher.HeightFor(offset):F4}.");

        if (ImGui.TreeNode("Sideways, forward and turn"))
        {
            var x = offset.X;
            if (ImGui.DragFloat("Left", ref x, 0.001f, -2f, 2f, "%.3f"))
                Set(mountId, offset with { X = x });

            var z = offset.Z;
            if (ImGui.DragFloat("Forward", ref z, 0.001f, -2f, 2f, "%.3f"))
                Set(mountId, offset with { Z = z });

            var r = offset.Rotation;
            if (ImGui.DragFloat("Turn (degrees)", ref r, 0.5f, -180f, 180f, "%.0f"))
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
            watcher.Release();
        }

        ImGui.TextColored(Dim, "Nothing ticked and nothing typed counts as \"leave it alone\".");
        ImGui.Unindent();
    }

    private void Set(uint mountId, MountOffset offset)
    {
        config.Offsets[mountId] = offset;
        config.Save();
        // Send it again on the next tick so the change shows up while dragging.
        watcher.Refresh();
    }
}
