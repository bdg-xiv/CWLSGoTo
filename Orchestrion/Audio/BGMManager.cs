using CheapLoc;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Orchestrion.BGMSystem;
using Orchestrion.InnSystem;
using Orchestrion.Ipc;
using Orchestrion.Persistence;
using Orchestrion.Types;

namespace Orchestrion.Audio;

public static class BGMManager
{
    // BGM 1 is "None" (an existing, silent scd) - the game BGM parks here while a
    // local file plays through LocalPlayer.
    private const int SilenceSongId = 1;

    private static readonly BGMController _bgmController;
    private static readonly OrchestrionInnController _innController;
    private static readonly OrchestrionIpcManager _ipcManager;
    
    private static bool _isPlayingReplacement;
    private static string _ddPlaylist;

    public delegate void SongChanged(int oldSong, int currentSong, int oldSecondSong, int oldCurrentSong, bool oldPlayedByOrch, bool playedByOrchestrion);
    public static event SongChanged OnSongChanged;

    public delegate void InnSongPlayed(string trackDtrName, string trackChatName);
    public static event InnSongPlayed OnInnSongPlayed;
    
    public static int CurrentSongId => _bgmController.CurrentSongId;
    public static int CurrentScene => _bgmController.CurrentScene;
    
    public static int OldSongId => _bgmController.OldSongId;
    public static int OldScene => _bgmController.OldScene;
    
    public static int PlayingSongId => _bgmController.PlayingSongId;
    public static int PlayingScene => _bgmController.PlayingScene;
    
    public static int SecondSongId => _bgmController.SecondSongId;
    public static int SecondScene => _bgmController.SecondScene;
    
    public static int CurrentAudibleSong => LocalPlayer.IsPlaying ? LocalPlayer.CurrentTrackId : _bgmController.CurrentAudibleSong;

    static BGMManager()
    {
        _bgmController = new BGMController();
        _innController = new OrchestrionInnController();
        _ipcManager = new OrchestrionIpcManager();

        DalamudApi.Framework.Update += Update;
        _bgmController.OnSongChanged += HandleSongChanged;
        _innController.OnPlayingSongChanged += HandleInnSongChanged;
        OnSongChanged += IpcUpdate;
    }

    public static void Dispose()
    {
        DalamudApi.Framework.Update -= Update;
        Stop();
        OnSongChanged -= IpcUpdate;
        _innController.OnPlayingSongChanged -= HandleInnSongChanged;
        _bgmController.OnSongChanged -= HandleSongChanged;
        _bgmController.Dispose();
    }

    private static void IpcUpdate(int oldSong, int newSong, int oldSecondSong, int oldCurrentSong, bool oldPlayedByOrch, bool playedByOrch)
    {
        _ipcManager.InvokeSongChanged(newSong);
        if (playedByOrch) _ipcManager.InvokeOrchSongChanged(newSong);
    }
    
    public static void Update(IFramework ignored)
    {
        _innController.Update();
        _bgmController.Update();
        LocalPlayer.Update();
        CombatMusicManager.Update();
    }
    
    private static void HandleInnSongChanged(uint oldInnPlayingTrackId, uint newInnPlayingTrackId, string oldInnTrackDtrName, string newInnTrackDtrName, string newInnTrackChatName)
    {
        if (newInnPlayingTrackId > 0)
        {
            // Estate orchestrions play over BGM, so trying to play BGM now is useless
            Stop();
            DalamudApi.PluginLog.Debug($"[BGMManager::HandleInnSongChanged] Inn song changed from {oldInnPlayingTrackId} - '{oldInnTrackDtrName}' to {newInnPlayingTrackId} - '{newInnTrackChatName}'");
            OnInnSongPlayed.Invoke(newInnTrackDtrName, newInnTrackChatName);
        }
        else
        {
            // Force update for BGM Manager
            HandleSongChanged(0, _bgmController.CurrentSongId, _bgmController.OldSecondSongId, _bgmController.SecondSongId);
        }
    }

    private static void HandleSongChanged(int oldSong, int newSong, int oldSecondSong, int newSecondSong)
    {
        // Estate orchestrions play over BGM, so trying to handle BGM now is useless
        if (InnMusicActive()) return;
        
        var currentChanged = oldSong != newSong;
        var secondChanged = oldSecondSong != newSecondSong;
        
        var newHasReplacement = Configuration.Instance.SongReplacements.TryGetValue(newSong, out var newSongReplacement);
        var newSecondHasReplacement = Configuration.Instance.SongReplacements.TryGetValue(newSecondSong, out var newSecondSongReplacement);
        
        if (currentChanged)
            DalamudApi.PluginLog.Debug($"[HandleSongChanged] Current Song ID changed from {_bgmController.OldSongId} to {_bgmController.CurrentSongId}");
        
        if (secondChanged)
            DalamudApi.PluginLog.Debug($"[HandleSongChanged] Second song ID changed from {_bgmController.OldSecondSongId} to {_bgmController.SecondSongId}");
        
        if (PlayingSongId != 0 && DeepDungeonModeActive())
        {
            if (!string.IsNullOrEmpty(_ddPlaylist) && !Configuration.Instance.Playlists.ContainsKey(_ddPlaylist)) // user deleted playlist
                Stop();
            else
                PlayRandomSong(_ddPlaylist);
            return;
        }

        if (PlayingSongId != 0 && !_isPlayingReplacement) return; // manually playing track
        if (secondChanged && !currentChanged && !_isPlayingReplacement) return; // don't care about behind song if not playing replacement

        if (!newHasReplacement) // user isn't playing and no replacement at all
        {
            if (PlayingSongId != 0)
                Stop();
            else
                // Play and Stop invoke OnSongChanged themselves
                InvokeSongChanged(oldSong, newSong, oldSecondSong, newSecondSong, oldPlayedByOrch: false, playedByOrch: false);
            return;
        }

        var toPlay = 0;
        
        DalamudApi.PluginLog.Debug($"[HandleSongChanged] Song ID {newSong} has a replacement of {newSongReplacement.ReplacementId}");
        
        // Handle 2nd changing when 1st has replacement of NoChangeId, only time it matters
        if (newSongReplacement.ReplacementId == SongReplacementEntry.NoChangeId)
        {
            if (secondChanged)
            {
                toPlay = newSecondSong;
                if (newSecondHasReplacement) toPlay = newSecondSongReplacement.ReplacementId;
                if (toPlay == SongReplacementEntry.NoChangeId) return; // give up
            }    
        }
        
        if (newSongReplacement.ReplacementId == SongReplacementEntry.NoChangeId && PlayingSongId == 0)
        {
            toPlay = oldSong; // no net BGM change
        }
        else if (newSongReplacement.ReplacementId != SongReplacementEntry.NoChangeId)
        {
            toPlay = newSongReplacement.ReplacementId;
        }
        
        Play(toPlay, isReplacement: true); // we only ever play a replacement here
    }

    public static void Play(int songId, bool isReplacement = false)
    {
        // Estate orchestrions play over BGM, so trying to play BGM now is useless
        if (InnMusicActive()) return;
        
        // Don't play anything if we're in a cutscene and the setting is off
        var inCutscene = DalamudApi.Condition[ConditionFlag.OccupiedInCutSceneEvent];
        if (Configuration.Instance.DisableInCutscenes && inCutscene && isReplacement)
        {
            DalamudApi.PluginLog.Debug($"[Play] Not playing {songId} due to cutscene setting");
            return;
        }

        var wasPlaying = PlayingSongId != 0 || LocalPlayer.IsPlaying;
        var oldSongId = CurrentAudibleSong;
        var secondSongId = _bgmController.SecondSongId;

        if (LocalMusicManager.IsLocalId(songId))
        {
            DalamudApi.PluginLog.Debug($"[Play] Playing local track {songId}");
            if (!LocalPlayer.Play(songId)) return;
            InvokeSongChanged(oldSongId, songId, secondSongId, oldSongId, oldPlayedByOrch: wasPlaying, playedByOrch: true);
            _bgmController.SetSong(SilenceSongId);
            _isPlayingReplacement = isReplacement;
            return;
        }

        if (LocalPlayer.IsPlaying)
            LocalPlayer.Stop();

        DalamudApi.PluginLog.Debug($"[Play] Playing {songId}");
        InvokeSongChanged(oldSongId, songId, secondSongId, oldSongId, oldPlayedByOrch: wasPlaying, playedByOrch: true);
        _bgmController.SetSong((ushort)songId);
        _isPlayingReplacement = isReplacement;
    }

    public static void Stop()
    {
        if (PlaylistManager.IsPlaying)
        {
            DalamudApi.PluginLog.Debug("[Stop] Stopping playlist...");    
            PlaylistManager.Reset();
        }

        _ddPlaylist = null;

        var localTrackId = LocalPlayer.CurrentTrackId;
        if (LocalPlayer.IsPlaying)
            LocalPlayer.Stop();

        if (PlayingSongId == 0) return;
        DalamudApi.PluginLog.Debug($"[Stop] Stopping playing {_bgmController.PlayingSongId}...");

        if (Configuration.Instance.SongReplacements.TryGetValue(_bgmController.CurrentSongId, out var replacement))
        {
            DalamudApi.PluginLog.Debug($"[Stop] Song ID {_bgmController.CurrentSongId} has a replacement of {replacement.ReplacementId}...");

            var toPlay = replacement.ReplacementId;
            
            if (toPlay == SongReplacementEntry.NoChangeId)
                toPlay = _bgmController.OldSongId;

            // Play will invoke OnSongChanged for us
            Play(toPlay, isReplacement: true);
            return;
        }

        var second = _bgmController.SecondSongId;

        // If there was no replacement involved, we don't need to do anything else, just stop
        var stoppedSongId = localTrackId != 0 ? localTrackId : PlayingSongId;
        InvokeSongChanged(stoppedSongId, CurrentSongId, second, second, oldPlayedByOrch: true, playedByOrch: false);
        _bgmController.SetSong(0);
    }

    private static void InvokeSongChanged(int oldSongId, int newSongId, int oldSecondSongId, int newSecondSongId, bool oldPlayedByOrch, bool playedByOrch)
    {
        DalamudApi.PluginLog.Debug($"[InvokeSongChanged] Invoking SongChanged event with {oldSongId} -> {newSongId}, {oldSecondSongId} -> {newSecondSongId} | {oldPlayedByOrch} {playedByOrch}");
        OnSongChanged?.Invoke(oldSongId, newSongId, oldSecondSongId, newSecondSongId, oldPlayedByOrch, playedByOrch);
    }

    public static void PlayRandomSong(string playlistName = "")
    {
        if (SongList.Instance.TryGetRandomSong(playlistName, out var randomFavoriteSong))
            Play(randomFavoriteSong);
        else
            DalamudApi.ChatGui.PrintError(Loc.Localize("NoPossibleSongs", "No possible songs found."));
    }

    public static void StartDeepDungeonMode(string playlistName = "")
    {
        _ddPlaylist = playlistName;
        PlayRandomSong(_ddPlaylist);
    }

    public static void StopDeepDungeonMode()
    {
        _ddPlaylist = null;
        Stop();
    }

    public static bool DeepDungeonModeActive()
    {
        return _ddPlaylist != null;
    }

    public static bool InnMusicActive()
    {
        return  _innController.IsInnTrackPlaying();
    }
}