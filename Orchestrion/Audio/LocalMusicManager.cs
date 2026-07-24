using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Vorbis;
using NAudio.Wave;
using Orchestrion.Persistence;
using Orchestrion.Types;

namespace Orchestrion.Audio;

/// <summary>
/// Scans the user's local music folder and injects the tracks into the SongList as
/// first-class songs (ids >= IdBase), so they mix with game songs in every list,
/// playlist and search. Playback of these ids is handled by LocalPlayer.
/// </summary>
public static class LocalMusicManager
{
	public const int IdBase = 100000;

	private static readonly string[] SupportedExtensions = { ".mp3", ".ogg", ".wav", ".m4a", ".aac", ".wma", ".flac" };

	// id -> full path of the scanned file; replaced wholesale on the framework thread after each scan
	private static Dictionary<int, string> _pathsById = new();
	private static volatile bool _scanning;

	public static event Action OnLibraryChanged;

	public static bool Scanning => _scanning;
	public static int TrackCount { get; private set; }
	public static string LastScanError { get; private set; } = string.Empty;

	public static bool IsLocalId(int id) => id >= IdBase;

	public static bool TryGetPath(int id, out string path) => _pathsById.TryGetValue(id, out path);

	public static bool IsSupportedFile(string path) => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

	public static void Rescan()
	{
		if (_scanning) return;
		_scanning = true;
		Task.Run(Scan).ContinueWith(t =>
		{
			_scanning = false;
			if (t.Exception != null)
				DalamudApi.PluginLog.Error(t.Exception, "[LocalMusicManager] Scan failed");
		});
	}

	private static void Scan()
	{
		var config = Configuration.Instance;
		var folder = config.LocalMusicFolder;
		var songs = new List<Song>();
		var paths = new Dictionary<int, string>();
		var error = string.Empty;

		if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
		{
			error = $"Folder not found: {folder}";
		}
		else if (!string.IsNullOrWhiteSpace(folder))
		{
			var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
				.Where(IsSupportedFile)
				.OrderBy(f => Path.GetRelativePath(folder, f), StringComparer.OrdinalIgnoreCase);

			var idsChanged = false;
			var skipped = 0;
			foreach (var file in files)
			{
				var relative = Path.GetRelativePath(folder, file);
				var key = relative.ToLowerInvariant();
				if (!config.LocalTrackIds.TryGetValue(key, out var id))
				{
					id = config.NextLocalTrackId++;
					config.LocalTrackIds[key] = id;
					idsChanged = true;
				}

				TimeSpan duration;
				try
				{
					using var reader = OpenFile(file);
					duration = reader.TotalTime;
				}
				catch (Exception e)
				{
					DalamudApi.PluginLog.Warning($"[LocalMusicManager] Could not read {relative}: {e.Message}");
					skipped++;
					continue;
				}

				songs.Add(BuildSong(id, file, relative, duration));
				paths[id] = file;
			}

			if (idsChanged)
				config.Save();
			if (skipped > 0)
				error = $"{skipped} file(s) could not be read; see the Dalamud log.";
		}

		DalamudApi.Framework.RunOnFrameworkThread(() =>
		{
			SongList.Instance.SetLocalSongs(songs);
			_pathsById = paths;
			TrackCount = songs.Count;
			LastScanError = error;
			DalamudApi.PluginLog.Information($"[LocalMusicManager] Loaded {songs.Count} local track(s)");
			OnLibraryChanged?.Invoke();
		});
	}

	private static Song BuildSong(int id, string fullPath, string relativePath, TimeSpan duration)
	{
		var title = Path.GetFileNameWithoutExtension(relativePath);
		var location = Path.GetDirectoryName(relativePath);
		if (string.IsNullOrEmpty(location)) location = "Local music";

		var strings = new SongStrings
		{
			Name = title,
			AlternateName = string.Empty,
			SpecialModeName = string.Empty,
			Locations = location,
			AdditionalInfo = "Local track",
		};

		var song = new Song
		{
			Id = id,
			FilePath = fullPath,
			FileExists = true,
			Duration = duration,
			DisableRestart = false,
			SpecialMode = 0,
		};
		// Every language Orchestrion knows must be present; various code paths index
		// Strings[lang] directly.
		foreach (var lang in Util.AvailableLanguages)
			song.Strings[lang] = strings;
		return song;
	}

	internal static WaveStream OpenFile(string path)
	{
		return Path.GetExtension(path).ToLowerInvariant() == ".ogg"
			? new VorbisWaveReader(path)
			: new AudioFileReader(path);
	}
}
