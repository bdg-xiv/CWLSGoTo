namespace ComplexTweaks.IPC;

public abstract class BaseIPC {
    public abstract string Name { get; }
    public abstract string Repo { get; }
    // patched from upstream: some plugins have forks that expose the same IPC surface
    // under a different internal name (e.g. BossModReborn); any of these satisfies
    // the availability check.
    public virtual IEnumerable<string> InternalNames => [Name];
    public bool IsLoaded => Svc.Interface.InstalledPlugins.Any(p => InternalNames.Contains(p.InternalName) && p.IsLoaded);

    public string Dynamis => "https://puni.sh/api/repository/";
    public string Punish => "https://love.puni.sh/ment.json";
    public string Main => string.Empty;
    public string Nightmare => "https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json";
    public string Kawaii => Dynamis + "kawaii";
    public string Veyn => Dynamis + "veyn";
    public string Vera => Dynamis + "vera";
}
