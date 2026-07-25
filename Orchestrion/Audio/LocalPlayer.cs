using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game.Config;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Orchestrion.Audio;

/// <summary>
/// Plays local music files through NAudio while the game's BGM is parked on the
/// silent placeholder track. Volume follows the in-game master/BGM sliders, and
/// replaced or stopped tracks fade out to match the game's own BGM transitions.
/// </summary>
public static class LocalPlayer
{
	private const int FadeOutMs = 1500;

	private sealed class Voice
	{
		public WaveOutEvent Output;
		public WaveStream Reader;
		public VolumeSampleProvider Volume;
		public float Gain = 1f;
		public long FadeStartedAt;
		public float FadeFromVolume;

		public void DisposeHard()
		{
			try
			{
				Output?.Stop();
				Output?.Dispose();
				Reader?.Dispose();
			}
			catch (Exception e)
			{
				DalamudApi.PluginLog.Warning($"[LocalPlayer] Dispose: {e.Message}");
			}
		}
	}

	private static Voice _current;
	private static readonly List<Voice> _fading = new();
	private static int _volumeRefreshCounter;
	private static float _configVolume = 1f;
	private static readonly int _gameProcessId = Process.GetCurrentProcess().Id;

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

	public static int CurrentTrackId { get; private set; }
	public static bool IsPlaying => CurrentTrackId != 0;

	public static TimeSpan Elapsed => _current?.Reader?.CurrentTime ?? TimeSpan.Zero;
	public static TimeSpan Duration => _current?.Reader?.TotalTime ?? TimeSpan.Zero;

	public static bool Play(int trackId)
	{
		FadeOutCurrent();

		if (!LocalMusicManager.TryGetPath(trackId, out var path))
		{
			DalamudApi.PluginLog.Warning($"[LocalPlayer] No file known for local track {trackId}");
			return false;
		}

		var voice = new Voice();
		try
		{
			_configVolume = GetGameBgmVolume();
			voice.Gain = LocalMusicManager.GetPlaybackGain(trackId);
			voice.Reader = LocalMusicManager.OpenFile(path);
			voice.Volume = new VolumeSampleProvider(voice.Reader.ToSampleProvider())
				{ Volume = (MutedByFocus() ? 0f : _configVolume) * voice.Gain };
			voice.Output = new WaveOutEvent();
			voice.Output.PlaybackStopped += (_, args) => OnPlaybackStopped(voice, args);
			voice.Output.Init(voice.Volume);
			voice.Output.Play();
			_current = voice;
			CurrentTrackId = trackId;
			DalamudApi.PluginLog.Debug($"[LocalPlayer] Playing {path}");
			return true;
		}
		catch (Exception e)
		{
			DalamudApi.PluginLog.Error(e, $"[LocalPlayer] Failed to play {path}");
			DalamudApi.ChatGui.PrintError($"[Orchestrion] Could not play local track: {e.Message}");
			voice.DisposeHard();
			return false;
		}
	}

	/// <summary>Stops the current track with a fade-out.</summary>
	public static void Stop()
	{
		FadeOutCurrent();
	}

	/// <summary>Immediately stops and disposes everything, including pending fades. For plugin unload.</summary>
	public static void Shutdown()
	{
		_current?.DisposeHard();
		_current = null;
		CurrentTrackId = 0;
		foreach (var voice in _fading)
			voice.DisposeHard();
		_fading.Clear();
	}

	private static void FadeOutCurrent()
	{
		var voice = _current;
		_current = null;
		CurrentTrackId = 0;
		if (voice == null) return;
		voice.FadeStartedAt = Environment.TickCount64;
		voice.FadeFromVolume = voice.Volume.Volume;
		_fading.Add(voice);
	}

	private static void OnPlaybackStopped(Voice voice, StoppedEventArgs args)
	{
		// Fires on NAudio's playback thread; only the active voice's natural end
		// matters (fading and replaced voices are handled by Update).
		DalamudApi.Framework.RunOnFrameworkThread(() =>
		{
			if (voice != _current || !IsPlaying) return;
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
		if (_fading.Count > 0)
		{
			var now = Environment.TickCount64;
			for (var i = _fading.Count - 1; i >= 0; i--)
			{
				var voice = _fading[i];
				var progress = (now - voice.FadeStartedAt) / (float)FadeOutMs;
				if (progress >= 1f)
				{
					voice.DisposeHard();
					_fading.RemoveAt(i);
				}
				else
				{
					voice.Volume.Volume = voice.FadeFromVolume * (1f - progress);
				}
			}
		}

		if (_current == null) return;
		if (++_volumeRefreshCounter >= 30)
		{
			_volumeRefreshCounter = 0;
			_configVolume = GetGameBgmVolume();
			// Re-read the gain so normalization/level setting changes apply live.
			_current.Gain = LocalMusicManager.GetPlaybackGain(CurrentTrackId);
		}

		// Like the game's own audio, go silent while the window is unfocused unless
		// the game is configured to keep playing.
		var target = (MutedByFocus() ? 0f : _configVolume) * _current.Gain;
		if (Math.Abs(_current.Volume.Volume - target) > 0.001f)
			_current.Volume.Volume = target;
	}

	private static bool MutedByFocus()
	{
		try
		{
			GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
			if (foregroundPid == _gameProcessId) return false;

			// Mirror "Play sounds while window is not active" plus its BGM sub-toggle.
			if (!(DalamudApi.GameConfig.TryGet(SystemConfigOption.IsSoundAlways, out bool soundAlways) && soundAlways))
				return true;
			return !(DalamudApi.GameConfig.TryGet(SystemConfigOption.IsSoundBgmAlways, out bool bgmAlways) && bgmAlways);
		}
		catch
		{
			return false;
		}
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
