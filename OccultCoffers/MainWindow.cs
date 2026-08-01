using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace OccultCoffers;

internal sealed class MainWindow : Window
{
    private readonly Tracker tracker;
    private readonly Configuration config;

    private static readonly Vector4 Confirmed = new(0.45f, 1.00f, 0.50f, 1f);
    private static readonly Vector4 Waiting = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    public MainWindow(Tracker tracker, Configuration config) : base("Occult Coffers###OccultCoffers")
    {
        this.tracker = tracker;
        this.config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public override void Draw()
    {
        if (tracker.Zone == null)
        {
            ImGui.TextColored(Dim, "Not in the Occult Crescent.");
            DrawSettings();
            return;
        }

        if (!tracker.SpotsLoaded)
        {
            ImGui.TextColored(Dim, "Reading the zone layout...");
            return;
        }

        ImGui.Text(tracker.Zone.Name);
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"- {tracker.Zone.FloorNameFor(tracker.CurrentMapId)}");

        if (tracker.SightAt is not { } sightAt)
        {
            ImGui.Separator();
            ImGui.TextWrapped("Cast Occult Treasuresight to take a reading. Until then there is nothing to narrow down.");
            ImGui.Spacing();
            ImGui.TextColored(Dim, $"{tracker.Spots.Count} spots in this zone " +
                                   $"({tracker.Of(CofferKind.Silver).Count()} silver, {tracker.Of(CofferKind.Bronze).Count()} bronze).");
            DrawSettings();
            return;
        }

        var age = DateTime.UtcNow - sightAt;
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"   reading is {Describe(age)} old");

        ImGui.Separator();

        DrawKind(CofferKind.Silver);
        ImGui.Spacing();
        DrawKind(CofferKind.Bronze);

        ImGui.Separator();
        if (ImGui.Button("Forget this reading"))
            tracker.Forget();
        ImGui.SameLine();
        ImGui.TextColored(Dim, "Re-cast Treasuresight for a fresh one.");

        DrawSettings();
    }

    private void DrawKind(CofferKind kind)
    {
        var reported = tracker.Reported(kind);
        var found = tracker.Found(kind);
        var outstanding = tracker.Outstanding(kind);
        var candidates = tracker.Candidates(kind);
        var confirmed = tracker.Confirmed(kind);

        ImGui.Text($"{kind}: {reported} reported");
        if (found > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"- {found} found -> {outstanding} left");
        }

        ImGui.Indent();
        if (reported == 0)
        {
            ImGui.TextColored(Dim, "None in the zone at the time of the reading.");
        }
        else if (confirmed.Count > 0)
        {
            ImGui.TextColored(Confirmed, $"{confirmed.Count} spot{(confirmed.Count == 1 ? "" : "s")} left and " +
                                         $"{outstanding} coffer{(outstanding == 1 ? "" : "s")} left - they are marked on the map.");
            foreach (var spot in confirmed.OrderBy(Distance))
            {
                var floor = tracker.Zone!.FloorNameFor(spot.MapId);
                ImGui.TextColored(Confirmed, $"  {floor}  ({spot.World.X:F0}, {spot.World.Z:F0})   {Distance(spot):F0}y");
            }
        }
        else if (outstanding == 0)
        {
            ImGui.TextColored(Dim, "All accounted for.");
        }
        else
        {
            ImGui.TextColored(Waiting, $"{candidates.Count} spots still unswept, {outstanding} coffers unaccounted for.");
            ImGui.TextColored(Dim, $"Sweep {candidates.Count - outstanding} more to pin them down.");
        }

        ImGui.Unindent();
    }

    private static float Distance(CofferSpot spot)
    {
        var player = ECommons.DalamudServices.Svc.Objects.LocalPlayer;
        if (player == null)
            return float.MaxValue;
        return Vector3.Distance(player.Position, spot.World);
    }

    private static string Describe(TimeSpan age)
        => age.TotalMinutes < 1 ? $"{age.TotalSeconds:F0}s" : $"{age.TotalMinutes:F0}m";

    private void DrawSettings()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Settings"))
            return;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        var candidates = config.ShowCandidates;
        if (ImGui.Checkbox("Mark spots not swept yet", ref candidates))
        {
            config.ShowCandidates = candidates;
            config.Save();
        }

        var confirmed = config.ShowConfirmed;
        if (ImGui.Checkbox("Mark confirmed coffers", ref confirmed))
        {
            config.ShowConfirmed = confirmed;
            config.Save();
        }

        var cleared = config.ShowCleared;
        if (ImGui.Checkbox("Mark spots already swept", ref cleared))
        {
            config.ShowCleared = cleared;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(a lot of icons)");

        var openOnSight = config.OpenWindowOnSight;
        if (ImGui.Checkbox("Open this window on a new reading", ref openOnSight))
        {
            config.OpenWindowOnSight = openOnSight;
            config.Save();
        }

        var radius = config.CheckRadius;
        if (ImGui.SliderFloat("Swept radius (yalms)", ref radius, 5f, 60f, "%.0f"))
        {
            config.CheckRadius = radius;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How close you have to pass a spot for it to count as checked.");

        if (tracker.Zone is { HasSubterrane: true })
        {
            var ceiling = config.SubterraneCeilingY;
            if (ImGui.SliderFloat("Subterrane ceiling (Y)", ref ceiling, -200f, 50f, "%.0f"))
            {
                config.SubterraneCeilingY = ceiling;
                config.Save();
                tracker.LeaveZone();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Spots below this altitude are put on the Subterrane map instead of the\n" +
                                 "North Basin one. The split below tells you whether it is right.");

            ImGui.TextDisabled($"Split: {tracker.Spots.Count(s => s.MapId == tracker.Zone!.SurfaceMapId)} in the " +
                               $"{tracker.Zone!.SurfaceName}, {tracker.Spots.Count(s => s.MapId == tracker.Zone.SubterraneMapId)} in the " +
                               $"{tracker.Zone.SubterraneName}.");
        }

        DrawIcon("Silver icon", () => config.SilverIcon, v => config.SilverIcon = v);
        DrawIcon("Bronze icon", () => config.BronzeIcon, v => config.BronzeIcon = v);
        DrawIcon("Unswept icon", () => config.CandidateIcon, v => config.CandidateIcon = v);
        DrawIcon("Swept icon", () => config.ClearedIcon, v => config.ClearedIcon = v);
    }

    private void DrawIcon(string label, Func<uint> get, Action<uint> set)
    {
        var value = (int)get();
        if (ImGui.InputInt(label, ref value))
        {
            set((uint)Math.Max(0, value));
            config.Save();
        }

        var texture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(get())).GetWrapOrDefault();
        if (texture != null)
        {
            ImGui.SameLine();
            ImGui.Image(texture.Handle, new Vector2(20, 20));
        }
    }
}
