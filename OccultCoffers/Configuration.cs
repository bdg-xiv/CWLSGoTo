using Dalamud.Configuration;

namespace OccultCoffers;

public class Configuration : IPluginConfiguration
{
    // Deliberately NOT the chest icons. Eureka Linker draws every possible coffer spot
    // using 60355 and 60356 for silver and bronze, so a confirmed coffer marked with the
    // same art is indistinguishable from the 80-odd places one merely might be. These are
    // the numbered attack markers - nothing else on an Occult Crescent map uses them.
    public const uint DefaultSilverIcon = 61201;
    public const uint DefaultBronzeIcon = 61202;
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

    public const float DefaultConfirmedIconSize = 40f;
    public const float DefaultCandidateIconSize = 24f;

    private const int CurrentVersion = 6;

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

    /// <summary>Anything below this altitude is taken to be on the Subterrane floor rather
    /// than the North Basin. Exposed because it is the one number here that is a judgement
    /// call rather than something the game hands over.</summary>
    public float SubterraneCeilingY { get; set; } = -10f;

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

        // The chest icons collide exactly with Eureka Linker's spot markers.
        if (SilverIcon == 60355)
            SilverIcon = DefaultSilverIcon;

        if (BronzeIcon == 60356)
            BronzeIcon = DefaultBronzeIcon;

        Version = CurrentVersion;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
