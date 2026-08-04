using System.Collections.Generic;
using Dalamud.Interface.Textures;

namespace HuntSpots;

/// <summary>
/// Icon ids are typed in by hand, so half-finished ones are normal - "60355" passes through
/// "6", "60", "603" on the way. Asking the texture provider for one of those throws, so
/// every id gets checked before anything is done with it.
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<uint, bool> Known = [];

    public static bool Exists(uint iconId)
    {
        if (iconId == 0)
            return false;

        if (Known.TryGetValue(iconId, out var known))
            return known;

        bool exists;
        try
        {
            exists = Plugin.TextureProvider.TryGetIconPath(new GameIconLookup(iconId), out _);
        }
        catch
        {
            exists = false;
        }

        Known[iconId] = exists;
        return exists;
    }

    public static uint OrFallback(uint iconId, uint fallback) => Exists(iconId) ? iconId : fallback;
}
