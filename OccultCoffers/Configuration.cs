using Dalamud.Configuration;

namespace OccultCoffers;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

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

    public uint SilverIcon { get; set; } = 60356;
    public uint BronzeIcon { get; set; } = 60355;
    public uint CandidateIcon { get; set; } = 60358;
    public uint ClearedIcon { get; set; } = 60354;

    public float ConfirmedIconSize { get; set; } = 32f;
    public float CandidateIconSize { get; set; } = 18f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
