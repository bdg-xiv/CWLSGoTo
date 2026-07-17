using ECommons.EzIpcManager;
using System.Threading.Tasks;

namespace ComplexTweaks.IPC;

#nullable disable
#pragma warning disable CS8632
[Ipc(Ipc.BossMod)]
public class BossModIPC : BaseIPC {
    public override string Name => "BossMod";
    public override string Repo => Veyn;
    // patched from upstream: BossModReborn registers the identical IPC surface under
    // the same "BossMod." prefix (verified: Presets.* and ObstacleMap.* endpoints,
    // VBM Default/VBM AI presets, and BossMod.Autorotation.* module type names all
    // match), so either plugin satisfies this integration.
    public override IEnumerable<string> InternalNames => ["BossMod", "BossModReborn"];
    public BossModIPC() => EzIPC.Init(this, Name);

    /// <remarks> string name </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, string?> Get;

    /// <remarks> string presetSerialized, bool overwrite </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, bool, bool> Create;

    /// <remarks> string name </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, bool> Delete;

    [EzIPC("Presets.%m", true)] public readonly Func<string> GetActive;

    /// <remarks> string name </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, bool> SetActive;
    [EzIPC("Presets.%m", true)] public readonly Func<bool> ClearActive;
    [EzIPC("Presets.%m", true)] public readonly Func<bool> GetForceDisabled;
    [EzIPC("Presets.%m", true)] public readonly Func<bool> SetForceDisabled;
    [EzIPC("Presets.%m", true)] public readonly Func<bool> Activate;
    [EzIPC("Presets.%m", true)] public readonly Func<bool> Deactivate;
    [EzIPC("Presets.%m", true)] public readonly Func<List<string>> GetActiveList;
    [EzIPC("Presets.%m", true)] public readonly Func<List<string>, bool> SetActiveList;

    /// <remarks> string presetName, string moduleTypeName, string trackName, string value </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, string, string, string, bool> AddTransientStrategy;

    /// <remarks> string presetName, string moduleTypeName, string trackName, string value, int oid </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, string, string, string, int, bool> AddTransientStrategyTargetEnemyOID;

    /// <remarks> string presetName, string moduleTypeName, string trackName </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, string, string, bool> ClearTransientStrategy;

    /// <remarks> string presetName, string moduleTypeName </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, string, bool> ClearTransientModuleStrategies;

    /// <remarks> string presetName </remarks>
    [EzIPC("Presets.%m", true)] public readonly Func<string, bool> ClearTransientPresetStrategies;

    /// <remarks> centerWorld, radius, writeToFile </remarks>
    [EzIPC("ObstacleMap.%m", true)] public readonly Func<Vector3, float, bool, bool> Generate;
    [EzIPC("ObstacleMap.%m", true)] public readonly Func<TaskStatus> GetGenerationStatus;
    [EzIPC("ObstacleMap.%m", true)] public readonly Func<bool> HasTempMap;
    [EzIPC("ObstacleMap.%m", true)] public readonly Func<bool> ClearTempMap;
    [EzIPC("ObstacleMap.%m", true)] public readonly Func<BitmapQuality?> EvaluateTempMapQuality;

    public readonly record struct BitmapQuality(
        float BlockedFraction, // amount of cells blocked (higher = less navigable)
        float LargestPassableComponentFraction, // amount of valid cells clustered in one area (higher = more navigable)
        float TinyPassableComponentFraction, // amount of valid cells in tiny clusters (higher = more fragmented)
        float SpeckleFraction, // amount of isolated cells with no neighbors of the same type (higher = noiser)
        int PassableComponents // count of passable regions (higher = more fragmented)
    ) {
        public bool BlockedIdeal => BlockedFraction < 0.85f;
        public bool LargestCompIdeal => LargestPassableComponentFraction < 0.5f;
        public bool TinyCompIdeal => TinyPassableComponentFraction < 0.03f;
        public bool SpeckleIdeal => SpeckleFraction < 0.003f;
        public bool IsBad => !BlockedIdeal || !LargestCompIdeal || !TinyCompIdeal || !SpeckleIdeal;
        public override string ToString() => $"Blocked: {BlockedFraction:P1}/{BlockedIdeal}, LargestComp: {LargestPassableComponentFraction:P1}/{LargestCompIdeal}, TinyComp: {TinyPassableComponentFraction:P1}/{TinyCompIdeal}, Speckle: {SpeckleFraction:P1}/{SpeckleIdeal}, PassableComps: {PassableComponents}";
    }

    public class Modules {
        public const string AutoFarm = "BossMod.Autorotation.MiscAI.AutoFarm";
    }
}
