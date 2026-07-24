using Dalamud.Game.Config;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Orchestrion.Audio;

/// <summary>
/// Plays local music files through NAudio while the game's BGM is parked on the
/// silent placeholder track. Volume follows the in-game master/BGM sliders.
/// </summary>
public static class LocalPlayer
{
	private static WaveOutEvent _output;
	private static WaveStream _reader;
	private static VolumeSampleProvider _volume;
	private static int _generation;
	private static int _volumeRefreshCounter;

	public static int CurrentTrackId { get; private set; }
	public static bool IsPlaying => CurrentTrackId != 0;

	public static TimeSpan Elapsed => _reader?.CurrentTime ?? TimeSpan.Zero;
	public static TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

	public static bool Play(int trackId)
	{
		Stop();

		if (!LocalMusicManager.TryGetPath(trackId, out var path))
		{
			DalamudApi.PluginLog.Warning($"[LocalPlayer] No file known for local track {trackId}");
			return false;
		}

		try
		{
			_reader = LocalMusicManager.OpenFile(path);
			_volume = new VolumeSampleProvider(_reader.ToSampleProvider()) { Volume = GetGameBgmVolume() };
			_output = new WaveOutEvent();
			var generation = _generation;
			_output.PlaybackStopped += (_, args) => OnPlaybackStopped(generation, args);
			_output.Init(_volume);
			_output.Play();
			CurrentTrackId = trackId;
			DalamudApi.PluginLog.Debug($"[LocalPlayer] Playing {path}");
			return true;
		}
		catch (Exception e)
		{
			DalamudApi.PluginLog.Error(e, $"[LocalPlayer] Failed to play {path}");
			DalamudApi.ChatGui.PrintError($"[Orchestrion] Could not play local track: {e.Message}");
			Cleanup();
			return false;
		}
	}

	public static void Stop()
	{
		_generation++; // invalidate pending PlaybackStopped callbacks
		Cleanup();
	}

	private static void Cleanup()
	{
		try
		{
			_output?.Stop();
			_output?.Dispose();
			_reader?.Dispose();
		}
		catch (Exception e)
		{
			DalamudApi.PluginLog.Warning($"[LocalPlayer] Cleanup: {e.Message}");
		}
		_output = null;
		_reader = null;
		_volume = null;
		CurrentTrackId = 0;
	}

	private static void OnPlaybackStopped(int generation, StoppedEventArgs args)
	{
		// Fires on NAudio's playback thread; only natural ends matter (manual stops
		// bump the generation first).
		DalamudApi.Framework.RunOnFrameworkThread(() =>
		{
			if (generation != _generation || !IsPlaying) return;
			if (args.Exception != null)
			{
				DalamudApi.PluginLog.Error(args.Exception, "[LocalPlayer] Playback error");
				DalamudApi.ChatGui.PrintError($"[Orchestrion] Local track playback failed: {args.Exception.Message}");
			}

			// While a playlist runs, PlaylistManager's duration timer advances to the
			// next song on its own; otherwise restore the game's BGM.
			if (PlaylistManager.IsPlaying) return;
			BGMManager.Stop();
		});
	}

	/// <summary>Called every framework tick (via BGMManager.Update).</summary>
	public static void Update()
	{
		if (!IsPlaying || _volume == null) return;
		if (++_volumeRefreshCounter < 30) return;
		_volumeRefreshCounter = 0;
		_volume.Volume = GetGameBgmVolume();
	}

	private static float GetGameBgmVolume()
	{
		try
		{
			if (DalamudApi.GameConfig.TryGet(SystemConfigOption.IsSndMaster, out bool masterMuted) && masterMuted) return 0f;
			if (DalamudApi.GameConfig.TryGet(SystemConfigOption.IsSndBgm, out bool bgmMuted) && bgmMuted) return 0f;
			var master = DalamudApi.GameConfig.TryGet(SystemConfigOption.SoundMaster, out uint masterVol) ? masterVol : 100u;
			var bgm = DalamudApi.GameConfig.TryGet(SystemConfigOption.SoundBgm, out uint bgmVol) ? bgmVol : 100u;
			return Math.Clamp(master / 100f * (bgm / 100f), 0f, 1f);
		}
		catch
		{
			return 1f;
		}
	}
}
