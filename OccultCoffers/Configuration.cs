using Dalamud.Configuration;

namespace OccultCoffers;

public class Configuration : IPluginConfiguration
{
    public const uint DefaultSilverIcon = 60355;
    public const uint DefaultBronzeIcon = 60356;
    public const uint DefaultCandidateIcon = 60358;

    // "Target to Ignore" - the crossed-out field marker. Swept and empty, nothing to
    // come back for, which is exactly what it looks like.
    public const uint DefaultClearedIcon = 61221;

    private const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Spots not yet walked past - the places a coffer could still be.</summary>
    public bool ShowCandidates { get; set; } = true;

    /// <summary>Spots the elimination has proven must hold a coffer.</summary>
    public bool ShowConfirmed { get; set; } = true;

    /// <summary>Spots already swept and found empty. Off by default - that is a lot of icons.</summary>
    public bool ShowCleared { get; set; }

    public bool OpenWindowOnSight { get; set; } = true;

    /// <summary>Keep our markers drawn above any other plugin's.</summary>
    public bool KeepMarkersOnTop { get; set; } = true;

    /// <summary>
    /// Work the check radius out from what the game actually streams in rather than from a
    /// number someone made up. BOCCHI has no radius at all - a coffer counts as detected the
    /// moment it lands in the object table - so the honest equivalent here is to measure how
    /// far away coffers have really been showing up and trust nothing beyond that.
    /// </summary>
    public bool AutoDetectionRange { get; set; } = true;

    /// <summary>Used until a coffer has actually been seen, and as the floor thereafter.
    /// Deliberately short: a spot wrongly called checked can be ruled out while a coffer is
    /// still sitting on it, and that is the one error that produces a wrong answer.</summary>
    public float MinDetectionRange { get; set; } = 15f;

    /// <summary>The radius when the automatic one is turned off.</summary>
    public float CheckRadius { get; set; } = 25f;

    /// <summary>Anything below this altitude is taken to be on the Subterrane floor rather
    /// than the North Basin. Exposed because it is the one number here that is a judgement
    /// call rather than something the game hands over.</summary>
    public float SubterraneCeilingY { get; set; } = -10f;

    public uint SilverIcon { get; set; } = DefaultSilverIcon;
    public uint BronzeIcon { get; set; } = DefaultBronzeIcon;
    public uint CandidateIcon { get; set; } = DefaultCandidateIcon;
    public uint ClearedIcon { get; set; } = DefaultClearedIcon;

    public float ConfirmedIconSize { get; set; } = 32f;
    public float CandidateIconSize { get; set; } = 18f;

    /// <summary>
    /// Version 1 shipped the silver and bronze icons the wrong way round and used a
    /// forgettable icon for swept spots. Only touch values still sitting on those old
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

        Version = CurrentVersion;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
