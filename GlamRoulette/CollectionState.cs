using System;
using System.Collections.Generic;
using System.Linq;

namespace GlamRoulette;

/// <summary>
/// What we have last written into a collection, mod by mod.
///
/// Penumbra's temporary settings belong to a collection rather than to a person - the "Player"
/// call only looks up the collection that object happens to be drawn with - so the way two people
/// wear the same mod differently is to set one person's options, redraw them so those options are
/// built into their model, and then set the next person's. Once a model is built it keeps what it
/// was built with, however the collection moves on underneath.
///
/// Which makes this the useful thing to know. A collection keeps its temporary settings across a
/// zone change even though every model in the zone is thrown away and rebuilt, so whoever's
/// options were loaded comes back correct on the other side for nothing. Without this we cannot
/// tell that apart from a stranger and pay for a redraw we did not need - which, on a hunt train,
/// is everyone, every teleport.
/// </summary>
internal sealed class CollectionState
{
    private readonly Dictionary<(Guid Collection, string Mod), (string Signature, bool Enabled)> written = [];

    /// <summary>Whether a collection is already set the way somebody wants it.</summary>
    public bool Holds(Guid collection, string mod, string signature)
        => written.TryGetValue((collection, mod), out var current) && current.Signature == signature;

    /// <summary>
    /// Whether the collection has this mod switched on to some set of options of our choosing,
    /// never mind whose. Anything we wrote is a set somebody was meant to be seen in, so for
    /// anyone but you it is a good enough answer to have been rebuilt into - which is the whole
    /// of what lets a crowd sharing one mod stop taking turns to redraw each other.
    /// </summary>
    public bool Carries(Guid collection, string mod)
        => written.TryGetValue((collection, mod), out var current) && current.Enabled;

    public void Wrote(Guid collection, string mod, string signature, bool enabled)
        => written[(collection, mod)] = (signature, enabled);

    /// <summary>Every collection we have put something into, for taking it all back out.</summary>
    public IReadOnlyList<Guid> Collections
        => written.Keys.Select(k => k.Collection).Distinct().ToList();

    public void Forget() => written.Clear();
}
