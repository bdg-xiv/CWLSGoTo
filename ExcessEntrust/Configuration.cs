using Dalamud.Configuration;

namespace ExcessEntrust;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Artisan crafting list whose material requirements are kept in the bags
    // (0 = no list selected; only AutoRetainer plan keeps apply).
    public int SelectedListId { get; set; }
    public string SelectedListName { get; set; } = string.Empty;
}
