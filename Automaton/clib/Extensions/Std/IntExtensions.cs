using clib.Services;
using System.Globalization;

namespace clib.Extensions;

public static class IntExtensions {
    public static Vector2 Vec2(this int i) => new(i);
    public static Vector3 Vec3(this int i) => new(i);
    public static Vector4 Vec4(this int i) => new(i);
    public static int Hex(this int i) => int.Parse(i.ToString("X"), NumberStyles.HexNumber);

    public static float Scaled(this int i) => i * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale * (Svc.Interface.UiBuilder.DefaultFontSpec.SizePt / 12f);
}
