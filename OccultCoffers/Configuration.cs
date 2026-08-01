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

    /// <summary>How close counts as having checked a spot. Coffers stream in well before
    /// this, but the eye needs a moment, so the default is deliberately conservative.</summary>
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
