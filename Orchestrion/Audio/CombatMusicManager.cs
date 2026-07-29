using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using Orchestrion.Persistence;

namespace Orchestrion.Audio;

/// <summary>
/// Switches to a designated playlist while fighting (any combat, hunt marks, or duty
/// bosses, per config) and restores the previous playlist - or the game BGM - a few
/// seconds after combat ends.
/// </summary>
public static class CombatMusicManager
{
	private const int EndDebounceMs = 4000;
	private const int HuntScanIntervalMs = 500;
	private const float HuntMarkRange = 80f;

	// ContentFinderCondition.ContentType: trials, raids, ultimate raids
	private static readonly uint[] BossContentTypes = { 4, 5, 28 };

	private static HashSet<uint> _nmNameIds;
	private static bool _active;
	private static string _restorePlaylist = string.Empty;
	private static int _restoreIndex = -1;
	private static long _triggerLostAt;
	private static long _lastHuntScan;
	private static bool _huntMarkEngaged;
	private static int _peacefulSongId;
	private static int _candidateSongId;
	private static long _candidateSince;
	private static bool _wasInCombat;
	private static uint _lastTerritory;
	private static uint _contentType;

	// A song must play this long out of combat before it becomes the "roaming"
	// baseline - boss themes that start moments before the pull can't poison it.
	private const int PeacefulStabilityMs = 5000;

	/// <summary>Called every framework tick (via BGMManager.Update).</summary>
	public static void Update()
	{
		var config = Configuration.Instance;
		if (!config.CombatPlaylistsEnabled)
		{
			if (_active) Deactivate();
			return;
		}

		var territory = DalamudApi.ClientState.TerritoryType;
		if (territory != _lastTerritory)
		{
			_lastTerritory = territory;
			_contentType = LookupContentType(territory);
			_peacefulSongId = 0;
			_candidateSongId = 0;
		}

		var now = Environment.TickCount64;
		var inCombat = DalamudApi.Condition[ConditionFlag.InCombat];
		// Not CurrentSongId: while a playlist plays, the scene holding the zone/boss
		// BGM is the one being overridden, so its song never reaches CurrentSongId.
		var naturalSong = BGMManager.NaturalSongId;

		// Track the game's own BGM as the "roaming" baseline, but only adopt a song
		// after it has played for a while out of combat: dungeon boss themes often
		// start seconds before the combat flag (door seal, intro, post-cutscene) and
		// must not become the baseline.
		//
		// Inside a duty the first settled song is the roaming theme and the baseline
		// is then frozen for the rest of the instance. Waiting out the timer is not
		// enough on its own: the boss theme starts on entering the arena, so buffing
		// or waiting for the party before pulling would otherwise let it be adopted,
		// after which the fight can never be told apart from roaming again.
		var baselineLocked = _peacefulSongId != 0 && DalamudApi.Condition[ConditionFlag.BoundByDuty];

		if (!inCombat && !baselineLocked && naturalSong != 0 && naturalSong != _peacefulSongId)
		{
			if (naturalSong != _candidateSongId)
			{
				_candidateSongId = naturalSong;
				_candidateSince = now;
			}
			else if (now - _candidateSince >= PeacefulStabilityMs)
			{
				_peacefulSongId = naturalSong;
				_candidateSongId = 0;
			}
		}

		if (inCombat && !_wasInCombat && DalamudApi.Condition[ConditionFlag.BoundByDuty])
			DalamudApi.PluginLog.Information(
				$"[CombatMusic] Duty pull: contentType {_contentType}, natural song {naturalSong}, "
				+ $"baseline {_peacefulSongId}, playing {BGMManager.PlayingSongId} on scene {BGMManager.PlayingScene}, "
				+ $"scenes {BGMManager.DescribeScenes()}");
		_wasInCombat = inCombat;

		var triggered = inCombat && (
			config.CombatTriggerAnyCombat
			|| (config.CombatTriggerHuntMarks && HuntMarkEngaged())
			|| (config.CombatTriggerDutyBosses && InDutyBossFight()));

		if (triggered)
		{
			_triggerLostAt = 0;
			if (!_active) Activate();
		}
		else if (_active)
		{
			if (_triggerLostAt == 0)
				_triggerLostAt = now;
			else if (now - _triggerLostAt > EndDebounceMs)
				Deactivate();
		}
	}

	private static void Activate()
	{
		var config = Configuration.Instance;
		if (!config.Playlists.TryGetValue(config.CombatPlaylistName ?? string.Empty, out var playlist)
		    || playlist.Songs.Count == 0)
			return;

		if (PlaylistManager.CurrentPlaylist?.Name == playlist.Name)
		{
			// Already listening to the combat playlist; nothing to restore later.
			_active = true;
			_restorePlaylist = string.Empty;
			_restoreIndex = -1;
			return;
		}

		_restorePlaylist = PlaylistManager.IsPlaying ? PlaylistManager.CurrentPlaylist.Name : string.Empty;
		_restoreIndex = PlaylistManager.IsPlaying ? PlaylistManager.CurrentSongIndex : -1;
		DalamudApi.PluginLog.Debug($"[CombatMusic] Fight started - playing '{playlist.Name}'");
		// Enter every fight on a random track from the combat playlist.
		PlaylistManager.Play(playlist.Name, Random.Shared.Next(playlist.Songs.Count));
		_active = true;
	}

	private static void Deactivate()
	{
		_active = false;
		_triggerLostAt = 0;
		var config = Configuration.Instance;

		// If the user changed music mid-fight, leave their choice alone.
		if (PlaylistManager.CurrentPlaylist?.Name != config.CombatPlaylistName)
			return;

		if (_restorePlaylist != string.Empty
		    && config.Playlists.TryGetValue(_restorePlaylist, out var playlist)
		    && playlist.Songs.Count > 0)
		{
			// Advance to the next song rather than replaying the interrupted one from
			// the start (game tracks cannot be resumed mid-song).
			DalamudApi.PluginLog.Debug($"[CombatMusic] Fight over - continuing '{playlist.Name}'");
			PlaylistManager.Resume(playlist.Name, Math.Min(_restoreIndex, playlist.Songs.Count - 1));
		}
		else
		{
			DalamudApi.PluginLog.Debug("[CombatMusic] Fight over - stopping combat playlist");
			PlaylistManager.Stop();
		}
	}

	private static bool HuntMarkEngaged()
	{
		var now = Environment.TickCount64;
		if (now - _lastHuntScan < HuntScanIntervalMs) return _huntMarkEngaged;
		_lastHuntScan = now;

		_nmNameIds ??= DalamudApi.DataManager.GetExcelSheet<NotoriousMonster>()!
			.Where(m => m.BNpcName.RowId > 0)
			.Select(m => m.BNpcName.RowId)
			.ToHashSet();

		_huntMarkEngaged = false;
		var player = DalamudApi.ObjectTable.LocalPlayer;
		if (player == null) return false;

		foreach (var obj in DalamudApi.ObjectTable)
		{
			if (obj is not IBattleNpc npc || npc.IsDead || !_nmNameIds.Contains(npc.NameId)) continue;
			if (!npc.StatusFlags.HasFlag(StatusFlags.InCombat)) continue;
			if (Vector3.Distance(player.Position, npc.Position) > HuntMarkRange) continue;
			_huntMarkEngaged = true;
			break;
		}

		return _huntMarkEngaged;
	}

	private static bool InDutyBossFight()
	{
		if (!DalamudApi.Condition[ConditionFlag.BoundByDuty]) return false;

		// Trials, raids and ultimates are a boss fight from the first pull.
		if (BossContentTypes.Contains(_contentType)) return true;

		// Dungeons (and other content): the game swapping its own BGM mid-combat is
		// the boss theme starting.
		var natural = BGMManager.NaturalSongId;
		return _peacefulSongId != 0 && natural != 0 && natural != _peacefulSongId;
	}

	private static uint LookupContentType(uint territory)
	{
		var row = DalamudApi.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
		return row?.ContentFinderCondition.ValueNullable?.ContentType.RowId ?? 0;
	}
}
