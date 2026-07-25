using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Orchestrion.Persistence;
using Orchestrion.Types;

namespace Orchestrion.Audio;

public static class PlaylistManager
{
	public static bool IsPlaying { get; private set; }
	public static Playlist? CurrentPlaylist => Configuration.Instance.Playlists.GetValueOrDefault(_currentPlaylist, null);
	public static int CurrentSongId => CurrentPlaylist?.Songs[_currentSongIndex] ?? 0;
	public static int CurrentSongIndex => _currentSongIndex;
	public static Song CurrentSong => SongList.Instance.GetSong(CurrentPlaylist?.Songs[_currentSongIndex] ?? 0);
	public static TimeSpan ElapsedDuration => TimeSpan.FromMilliseconds(ElapsedMs);
	public static TimeSpan Duration => CurrentSong.Duration;

	private static string _currentPlaylist = string.Empty;
	private static int _playlistStartTrackCount;
	private static int _currentSongIndex;
	private static int _playlistTrackPlayCount;
	private static readonly List<int> _playbackHistory = new();
	private static int _indexInHistory = -1;
	private static long _currentSongStartTime;
	private static long CurrentSongEndTime => IsPlaying ? 0 : _currentSongStartTime + (int) CurrentSong.Duration.TotalMilliseconds;
	private static long ElapsedMs => Environment.TickCount64 - _currentSongStartTime;
	private static long RemainingMs => CurrentSongEndTime - Environment.TickCount64;

	static PlaylistManager()
	{
		DalamudApi.Framework.Update += Update;
	}
	
	public static void Dispose()
	{
		DalamudApi.Framework.Update -= Update;
	}

	private static void Update(IFramework ignore)
	{
		if (_currentPlaylist == string.Empty || CurrentPlaylist == null) return;
		if (_playlistStartTrackCount != CurrentPlaylist.Songs.Count)
			ResetHistory();

		if (ElapsedDuration <= Duration) return;
		DalamudApi.PluginLog.Debug($"{ElapsedDuration} > {Duration}");

		Next();
	}
	
	public static void Play(string playlistName)
	{
		if (BGMManager.InnMusicActive()) return;
		Set(playlistName, -1, isPlaying: true);
		Next();
	}

	public static void Play(string playlistName, int index)
	{
		if (BGMManager.InnMusicActive()) return;
		Set(playlistName, index, isPlaying: true);
		BGMManager.Play(CurrentPlaylist.Songs[index]);
		_currentSongStartTime = Environment.TickCount64;
	}

	/// <summary>
	/// Starts the playlist continuing after lastIndex (the song that was interrupted),
	/// honoring the playlist's shuffle/repeat modes. Pass -1 to start fresh.
	/// </summary>
	public static void Resume(string playlistName, int lastIndex)
	{
		if (BGMManager.InnMusicActive()) return;
		Set(playlistName, lastIndex, isPlaying: true);
		Next();
	}

	private static void Set(string playlistName, int index, bool isPlaying)
	{
		_currentPlaylist = playlistName;
		_playlistStartTrackCount = CurrentPlaylist?.Songs.Count ?? 0;
		_currentSongIndex = index;
		_playlistTrackPlayCount = 0;
		_playbackHistory.Clear();
		_indexInHistory = -1;
		IsPlaying = isPlaying;

		// Remember the last playlist the user was listening to for auto-resume on
		// login; the combat playlist's takeovers don't count.
		if (isPlaying && playlistName != string.Empty
		    && playlistName != Configuration.Instance.CombatPlaylistName
		    && Configuration.Instance.LastPlayingPlaylist != playlistName)
		{
			Configuration.Instance.LastPlayingPlaylist = playlistName;
			Configuration.Instance.Save();
		}
	}

	private static void ResetHistory()
	{
		_playlistStartTrackCount = CurrentPlaylist?.Songs.Count ?? 0;
		_playbackHistory.Clear();
		_indexInHistory = -1;
	}

	public static void Previous()
	{
		if (_currentPlaylist == string.Empty || CurrentPlaylist == null) return;
		BeginTrack(GetPreviousSong());
	}

	public static void Next()
	{
		if (_currentPlaylist == string.Empty || CurrentPlaylist == null) return;
		BeginTrack(GetNextSong());
	}

	private static void BeginTrack(int id)
	{
		DalamudApi.PluginLog.Debug($"[PlaylistManager] [BeginTrack] id: {id} _currentSongIndex {_currentSongIndex}");
		if (id == 0)
			Stop();
		else
		{
			BGMManager.Play(id);
			_currentSongStartTime = Environment.TickCount64;
		}
	}
	
	/// <summary>Moves the currently playing song from the current playlist to the given playlist, then advances.</summary>
	public static void MoveCurrentToPlaylist(Playlist target)
	{
		var playlist = CurrentPlaylist;
		if (!IsPlaying || playlist == null || target == null || target == playlist) return;

		var index = _currentSongIndex;
		if (index < 0 || index >= playlist.Songs.Count) return;

		target.AddSong(playlist.Songs[index]);
		RemoveCurrentAndSkip();
	}

	/// <summary>Removes the currently playing song from the current playlist and advances.</summary>
	public static void RemoveCurrentAndSkip()
	{
		var playlist = CurrentPlaylist;
		if (!IsPlaying || playlist == null) return;

		var index = _currentSongIndex;
		if (index >= 0 && index < playlist.Songs.Count)
			playlist.RemoveSong(index);

		ResetHistory();

		if (playlist.Songs.Count == 0)
		{
			Stop();
			return;
		}

		// Aim the index so the advance lands on the song that slid into the removed
		// slot; shuffle and repeat modes still follow their own rules in GetNextSong.
		_currentSongIndex = playlist.RepeatMode == RepeatMode.One
			? index % playlist.Songs.Count
			: index - 1;
		BeginTrack(GetNextSong());
	}

	public static void Stop()
	{
		BGMManager.Stop();
		Set("", -1, isPlaying: false);

		// A deliberate stop also means "don't auto-resume next session".
		if (Configuration.Instance.LastPlayingPlaylist != string.Empty)
		{
			Configuration.Instance.LastPlayingPlaylist = string.Empty;
			Configuration.Instance.Save();
		}
	}
	
	public static void Reset()
	{
		Set("", -1, isPlaying: false);
	}

	private static int GetNextSong()
	{
		if (!IsPlaying) return 0;

		_playlistTrackPlayCount++;
		
		if (_playbackHistory.Count > 0 && _indexInHistory != _playbackHistory.Count - 1)
			return CurrentPlaylist.Songs[_playbackHistory[++_indexInHistory]];
		
		DalamudApi.PluginLog.Debug($"[PlaylistManager] [GetNextSong] CurrentPlaylist.RepeatMode: {CurrentPlaylist?.RepeatMode} CurrentPlaylist.ShuffleMode: {CurrentPlaylist?.ShuffleMode}");
		var nextSong = GenerateNextSongId();
		_playbackHistory.Add(_currentSongIndex);
		_indexInHistory++;
		return nextSong;
	}

	private static int GenerateNextSongId()
	{
		switch (CurrentPlaylist?.RepeatMode)
		{
			case RepeatMode.One:
				if (_currentSongIndex == -1) _currentSongIndex += 1;
				return CurrentPlaylist.Songs[_currentSongIndex];
			case RepeatMode.All when CurrentPlaylist.ShuffleMode == ShuffleMode.Off:
				_currentSongIndex = ++_currentSongIndex % CurrentPlaylist.Songs.Count;
				return CurrentPlaylist.Songs[_currentSongIndex];
			case RepeatMode.All when CurrentPlaylist.ShuffleMode == ShuffleMode.On:
				_currentSongIndex = Random.Shared.Next(CurrentPlaylist.Songs.Count);
				return CurrentPlaylist.Songs[_currentSongIndex];
			case RepeatMode.Once when _playlistTrackPlayCount < CurrentPlaylist.Songs.Count:
				if (_currentSongIndex + 1 >= CurrentPlaylist.Songs.Count)
					return 0;
				return CurrentPlaylist.Songs[++_currentSongIndex];
			case RepeatMode.Once when _playlistTrackPlayCount >= CurrentPlaylist.Songs.Count:
				return 0;
			default:
				return 0;
		}
	}

	private static int GetPreviousSong()
	{
		if (_indexInHistory <= 0 || _playbackHistory.Count == 0)
			return 0;
		_currentSongIndex = _playbackHistory[--_indexInHistory];
		return CurrentPlaylist.Songs[_currentSongIndex];
	}
}