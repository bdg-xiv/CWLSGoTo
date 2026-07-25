using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Orchestrion.Persistence;

public class Playlist
{
	public string Name { get; set; }
	public string DisplayName { get; set; }
	public List<int> Songs { get; set; }

	// Persisted via the backing fields: the property setters call Configuration.Save,
	// which would recurse into config loading during deserialization.
	[JsonProperty("RepeatMode")]
	private RepeatMode _repeatMode;

	[JsonIgnore]
	public RepeatMode RepeatMode {
		get => _repeatMode;
		set
		{
			_repeatMode = value;
			Configuration.Instance.Save();
		}
	}

	[JsonProperty("ShuffleMode")]
	private ShuffleMode _shuffleMode;

	[JsonIgnore]
	public ShuffleMode ShuffleMode {
		get => _shuffleMode;
		set
		{
			_shuffleMode = value;
			Configuration.Instance.Save();
		}
	}

	[OnDeserializing]
	internal void OnDeserializing(StreamingContext context)
	{
		// Configs from before the modes were persisted default to All/Off, not One.
		_repeatMode = RepeatMode.All;
		_shuffleMode = ShuffleMode.Off;
	}
	
	[JsonIgnore]
	public bool PendingDelete { get; set; }
	
	public Playlist() { }
	
	public Playlist(string name)
	{
		RepeatMode = RepeatMode.All;
		ShuffleMode = ShuffleMode.Off;
		
		Name = name;
		Songs = new List<int>();
	}
	
	public Playlist(string name, List<int> songs) : this(name)
	{
		Songs = songs;
	}
	
	public void NextRepeatMode()
	{
		RepeatMode = (RepeatMode) ((int)(RepeatMode + 1) % 3);
	}
	
	public void NextShuffleMode()
	{
		ShuffleMode = (ShuffleMode) ((int)(ShuffleMode + 1) % 2);
	}

	public void AddSong(int songId)
	{
		Songs.Add(songId);
		Configuration.Instance.Save();
	}
	
	public void AddSongs(IEnumerable<int> songIds)
	{
		Songs.AddRange(songIds);
		Configuration.Instance.Save();
	}
	
	public void RemoveSong(int index)
	{
		Songs.RemoveAt(index);
		Configuration.Instance.Save();
	}
}