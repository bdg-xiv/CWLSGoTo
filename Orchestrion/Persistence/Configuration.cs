using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using Orchestrion.Types;

namespace Orchestrion.Persistence;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool ShowSongInTitleBar { get; set; } = true;
    public bool ShowSongInChat { get; set; } = true;
    public bool ShowIdInNative { get; set; } = false;
    public bool ShowSongInNative { get; set; } = true;
    public bool HandleSpecialModes { get; set; } = true;
    public bool ShowFilePaths { get; set; } = true;
    public bool PlaylistPaneOpen { get; set; } = true;
    public bool ShowMiniPlayer { get; set; } = false;
    public bool MiniPlayerLock { get; set; } = false;
    public float MiniPlayerOpacity { get; set; } = 1.0f;

    public bool ChatChannelMatchDalamud { get; set; } = true;
    public bool ShowAltLangTitles { get; set; } = false;
    public bool UserInterfaceLanguageMatchDalamud { get; set; } = true;
    public string UserInterfaceLanguageCode { get; set; } = DalamudApi.PluginInterface.UiLanguage;
    public string AltTitleLanguageCode { get; set; } = "ja";
    public string ServerInfoLanguageCode { get; set; } = "en";
    public string ChatLanguageCode { get; set; } = "en";
    public XivChatType ChatType { get; set; } = DalamudApi.PluginInterface.GeneralChatType;
    public bool DisableTooltips { get; set; } = false;
    public bool DisableFurnishingMessages { get; set; } = false;
    public bool DisableInCutscenes { get; set; } = false;
    public bool DisableReplacementsInCutscenes { get; set; } = false;
    
    public string LastSelectedPlaylist { get; set; } = "Favorites";

    public Dictionary<int, SongReplacementEntry> SongReplacements { get; private set; } = new();
    
    [Obsolete("Favorites are gone in favor of playlists.")]
    public HashSet<int> FavoriteSongs { get; internal set; } = new();
    
    public Dictionary<string, Playlist> Playlists { get; set; } = new();

    // Local music: folder to scan, plus a stable relative-path -> song id mapping so
    // playlist entries survive rescans and restarts.
    public string LocalMusicFolder { get; set; } = string.Empty;
    public Dictionary<string, int> LocalTrackIds { get; set; } = new();
    public int NextLocalTrackId { get; set; } = 100000;

    // Songs ever auto-added to the "Everything" playlist; re-seeding only adds ids
    // not in here, so songs the user pruned are not restored.
    public HashSet<int> EverythingPlaylistSeeded { get; set; } = new();

    // Volume normalization for local music: per-file loudness cache (keyed like
    // LocalTrackIds) plus a user level to align local tracks with the game BGM.
    public bool NormalizeLocalMusic { get; set; } = true;
    public float LocalMusicLevel { get; set; } = 1.0f;
    public Dictionary<string, LocalTrackLoudness> LocalTrackLoudness { get; set; } = new();

    // Auto-resume: restart the last playing playlist after login.
    public bool AutoResumeLastPlaylist { get; set; } = true;
    public string LastPlayingPlaylist { get; set; } = string.Empty;

    // Advance the playing playlist to its next song on every zone change.
    public bool NextSongOnZoneChange { get; set; } = true;

    // Combat music: switch to a chosen playlist while fighting, per the triggers below.
    public bool CombatPlaylistsEnabled { get; set; } = false;
    public string CombatPlaylistName { get; set; } = string.Empty;
    public bool CombatTriggerAnyCombat { get; set; } = false;
    public bool CombatTriggerHuntMarks { get; set; } = true;
    public bool CombatTriggerDutyBosses { get; set; } = true;

    private Configuration() { }

    [JsonIgnore]
    private static Configuration _instance;
    
    [JsonIgnore]
    public static Configuration Instance {
        get
        {
            _instance ??= DalamudApi.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Migrate(_instance);
            return _instance;
        }
    }

    public bool TryGetPlaylist(string playlistName, out Playlist foundPlaylist)
    {
        foundPlaylist = null;
        foreach (var pInfo in Playlists) {
            if (playlistName.Equals(pInfo.Key, StringComparison.InvariantCultureIgnoreCase))
            {
                foundPlaylist = pInfo.Value;
                return true;
            }
        }
        return false;
    }

    public void DeletePlaylist(string playlistName)
    {
        Playlists.Remove(playlistName);
        Save();
    }

    private static void Migrate(Configuration c)
    {
        switch (c.Version)
        {
            case 1:
                c.Version = 2;
                c.Playlists = new Dictionary<string, Playlist>
                {
                    {"Favorites", new Playlist("Favorites", c.FavoriteSongs.ToList())},
                };
                c.Save();
                break;
        }
    }

    public void Save()
    {
        DalamudApi.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public class LocalTrackLoudness
{
    public long Size { get; set; }
    public float Loudness { get; set; }
    public float Peak { get; set; }
}