using Dalamud.Configuration;

namespace OccultCoffers;

public class Configuration : IPluginConfiguration
{
    // Neither a chest nor a number. Chests are what an unswept spot already looks like, and
    // the numbered attack markers read as waymarks 1 and 2 on a map where people place
    // waymarks. Triangle and plus are neither, and nothing else draws them here.
    public const uint DefaultSilverIcon = 61234;
    public const uint DefaultBronzeIcon = 61233;
    // The chest icons, matching Eureka Linker's - an unswept spot is exactly what its
    // markers mean, so they should read the same. Confirmed coffers are what needs to
    // stand apart, and those use the attack markers below.
    public const uint DefaultSilverCandidateIcon = 60355;
    public const uint DefaultBronzeCandidateIcon = 60356;

    // Only still here to migrate anyone off it.
    private const uint LegacyCandidateIcon = 61232;

    // "Target to Ignore" - the crossed-out field marker. Swept and empty, nothing to
    // come back for, which is exactly what it looks like.
    public const uint DefaultClearedIcon = 61221;

    // The size Eureka Linker's chest markers come out at, so ours line up with them rather
    // than sitting proud of them. Telling them apart is the shape's job now, not the size's.
    public const float DefaultConfirmedIconSize = 32f;
    public const float DefaultCandidateIconSize = 32f;

    private const int CurrentVersion = 8;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Spots not yet walked past - the places a coffer could still be.</summary>
    public bool ShowCandidates { get; set; } = true;

    /// <summary>Spots the elimination has proven must hold a coffer.</summary>
    public bool ShowConfirmed { get; set; } = true;

    /// <summary>Spots already swept and found empty. Off by default - that is a lot of icons.</summary>
    public bool ShowCleared { get; set; }

    public bool OpenWindowOnSight { get; set; } = true;

    /// <summary>
    /// Work the check radius out from what the game actually streams in rather than from a
    /// number someone made up. BOCCHI has no radius at all - a coffer counts as detected the
    /// moment it lands in the object table - so the honest equivalent here is to measure how
    /// far away coffers have really been showing up and trust nothing beyond that.
    /// </summary>
    public bool AutoDetectionRange { get; set; } = true;

    /// <summary>Used until a coffer has actually appeared, and as the lower clamp thereafter,
    /// so one freak short sighting cannot collapse the radius to nothing.</summary>
    public float MinDetectionRange { get; set; } = 15f;

    /// <summary>The radius when the automatic one is turned off.</summary>
    public float CheckRadius { get; set; } = 25f;

    /// <summary>
    /// Anything below this altitude is taken to be on the Subterrane floor rather than the
    /// North Basin. The game does not state which floor a layout instance belongs to, so this
    /// is a judgement call - but not a blind one: cross-checked against Eureka Linker's
    /// hand-maintained table, North Basin coffers run down to Y -21.8 and Subterrane ones
    /// start at Y -92, so anywhere in that 70-yalm gap is safe and the middle is safest.
    /// </summary>
    public float SubterraneCeilingY { get; set; } = DefaultSubterraneCeilingY;

    public const float DefaultSubterraneCeilingY = -60f;

    public uint SilverIcon { get; set; } = DefaultSilverIcon;
    public uint BronzeIcon { get; set; } = DefaultBronzeIcon;
    public uint SilverCandidateIcon { get; set; } = DefaultSilverCandidateIcon;
    public uint BronzeCandidateIcon { get; set; } = DefaultBronzeCandidateIcon;
    public uint ClearedIcon { get; set; } = DefaultClearedIcon;

    /// <summary>Superseded by the per-rarity pair above; kept so old settings can be read.</summary>
    public uint CandidateIcon { get; set; }

    internal uint CandidateIconFor(CofferKind kind)
        => kind == CofferKind.Silver ? SilverCandidateIcon : BronzeCandidateIcon;

    internal uint DefaultCandidateIconFor(CofferKind kind)
        => kind == CofferKind.Silver ? DefaultSilverCandidateIcon : DefaultBronzeCandidateIcon;

    public float ConfirmedIconSize { get; set; } = DefaultConfirmedIconSize;
    public float CandidateIconSize { get; set; } = DefaultCandidateIconSize;

    /// <summary>
    /// Version 1 shipped the silver and bronze icons the wrong way round and used forgettable
    /// icons for swept and unswept spots. Only touch values still sitting on those old
    /// defaults - a choice someone actually made stays made.
    /// </summary>
    public void Migrate()
    {
        if (Version >= CurrentVersion)
            return;

        if (SilverIcon == 60356 && BronzeIcon == 60355)
        {
            SilverIcon = DefaultSilverIcon;
            BronzeIcon = DefaultBronzeIcon;
        }

        if (ClearedIcon == 60354)
            ClearedIcon = DefaultClearedIcon;

        // Unswept spots used to be one icon for both rarities. A value that was left alone
        // becomes the new per-rarity pair; one that was chosen deliberately is carried into
        // both, so nothing silently reverts to something the user did not pick.
        if (CandidateIcon is not (0 or 60358 or LegacyCandidateIcon))
        {
            SilverCandidateIcon = CandidateIcon;
            BronzeCandidateIcon = CandidateIcon;
        }
        CandidateIcon = 0;

        // Bigger by default now that we know something else is drawing chests on the same
        // spots through the same overlay, and the draw order between the two is not ours
        // to decide. Size is the only lever that always works.
        if (ConfirmedIconSize == 32f)
            ConfirmedIconSize = DefaultConfirmedIconSize;

        if (CandidateIconSize == 18f)
            CandidateIconSize = DefaultCandidateIconSize;

        // 60355/60356 collided with Eureka Linker's spot markers; 61201/61202 replaced them
        // and turned out to read as waymarks 1 and 2.
        if (SilverIcon is 60355 or 61201)
            SilverIcon = DefaultSilverIcon;

        if (BronzeIcon is 60356 or 61202)
            BronzeIcon = DefaultBronzeIcon;

        // Sized to sit level with the chest markers rather than proud of them.
        if (ConfirmedIconSize == 40f)
            ConfirmedIconSize = DefaultConfirmedIconSize;

        if (CandidateIconSize == 24f)
            CandidateIconSize = DefaultCandidateIconSize;

        // -10 cut through the North Basin rather than between the floors, so every surface
        // spot below it was being drawn on the Subterrane map and was simply missing.
        if (SubterraneCeilingY == -10f)
            SubterraneCeilingY = DefaultSubterraneCeilingY;

        Version = CurrentVersion;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
