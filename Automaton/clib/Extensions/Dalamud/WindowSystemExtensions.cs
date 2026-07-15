using Dalamud.Interface.Windowing;
using System.Diagnostics.CodeAnalysis;

namespace clib.Extensions;

public static class WindowSystemExtensions {
    public static Window? GetWindow<T>(this WindowSystem ws) where T : Window => ws.Windows.OfType<T>().FirstOrDefault();
    public static bool TryGetWindow<T>(this WindowSystem ws, [NotNullWhen(true)] out Window? window) where T : Window {
        window = ws.GetWindow<T>();
        return window != null;
    }
    public static void Toggle<T>(this WindowSystem ws) where T : Window => GetWindow<T>(ws)?.IsOpen ^= true;
}
