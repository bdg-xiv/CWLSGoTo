using Dalamud.Utility;
using Lumina.Text.ReadOnly;
using System.Text;

namespace clib.Extensions;

public static class ReadOnlySeStringExtensions {
    public static bool ContainsText(this ReadOnlySeString seString, string needle) {
        if (seString.IsEmpty) return false;
        var bytes = new ReadOnlySpan<byte>(Encoding.UTF8.GetBytes(needle));
        return SeStringExtensions.ContainsText(seString, bytes);
    }

    public static bool ContainsAny(this ReadOnlySeString seString, IEnumerable<string> needles) {
        if (seString.IsEmpty) return false;
        foreach (var n in needles) {
            var bytes = new ReadOnlySpan<byte>(Encoding.UTF8.GetBytes(n));
            if (SeStringExtensions.ContainsText(seString, bytes))
                return true;
        }
        return false;
    }
}
