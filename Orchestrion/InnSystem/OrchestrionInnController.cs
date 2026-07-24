using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game;
using Orchestrion.Persistence;

namespace Orchestrion.InnSystem;

internal class OrchestrionInnController
{
    private readonly Lock _innTrackDataAccessLock = new();

    private OrchestrionPlayMode _newInnPlayMode = OrchestrionPlayMode.PlayAll;
    private OrchestrionMode _newInnMode = OrchestrionMode.Off;
    private uint _newInnSamplingTrackId;
    private uint _newInnPlayingTrackId;
    private string _newInnTrackDtrName = string.Empty;
    private string _newInnTrackChatName = string.Empty;

    private OrchestrionPlayMode _oldPlayMode = OrchestrionPlayMode.PlayAll;
    private OrchestrionMode _oldInnMode = OrchestrionMode.Off;
    private uint _oldInnSamplingTrackId;
    private uint _oldInnPlayingTrackId;
    private string _oldInnTrackDtrName = string.Empty;
    private string _oldInnTrackChatName = string.Empty;

    internal delegate void InnPlayingSongChangedHandler(
        uint oldPlayingTrackId, uint newPlayingTrackId,
        string oldInnTrackDtrName, string newInnTrackDtrName, string newInnTrackChatName
    );
    internal event InnPlayingSongChangedHandler? OnPlayingSongChanged;

    internal OrchestrionInnController()
    {
        ResetAllFieldValues();
    }

    internal bool IsInnTrackPlaying() => _newInnMode != OrchestrionMode.Off;
    internal bool IsInnTrackSampling() => _newInnMode == OrchestrionMode.Sampling;

    internal unsafe void Update()
    {
        lock (_innTrackDataAccessLock)
        {
            var innTrackChanged = UpdateFieldValues();

            if (innTrackChanged)
                HandleNewInnTrack();

            RecordOldFieldValues();
        }
    }

    private void HandleNewInnTrack()
    {
        var serverInfoLanguageCode = Configuration.Instance.ServerInfoLanguageCode;
        var chatLanguageCode = Configuration.Instance.ChatLanguageCode;

        if (IsInnTrackPlaying())
        {
            _newInnTrackDtrName = GetInnTrackName(_newInnPlayingTrackId, serverInfoLanguageCode);
            _newInnTrackChatName = GetInnTrackName(_newInnPlayingTrackId, chatLanguageCode);
        }

        OnPlayingSongChanged?.Invoke(
            _oldInnPlayingTrackId, _newInnPlayingTrackId,
            _oldInnTrackDtrName, _newInnTrackDtrName, _newInnTrackChatName
        );
    }

    private string GetInnTrackName(uint trackId, string languageCode)
    {
        string innTrackName;

        try
        {
            innTrackName = DalamudApi.DataManager.Excel.GetSheet<Lumina.Excel.Sheets.Orchestrion>(
                language: Util.LangCodeToLuminaLanguage(languageCode)
            ).GetRow(trackId).Name.ToString();
        }
        catch (Exception ex)
        {
            DalamudApi.PluginLog.Warning(ex, $"[OrchestrionInnController::GetInnTrackName] Exception thrown while looking up chat name for new inn song with track ID {trackId} and language code {languageCode}, falling back to English");
            innTrackName = DalamudApi.DataManager.Excel.GetSheet<Lumina.Excel.Sheets.Orchestrion>(
                language: Lumina.Data.Language.English
            ).GetRow(trackId).Name.ToString();
        }

        return $"{{{innTrackName}}}";
    }

    private unsafe bool UpdateFieldValues()
    {
        var manager = OrchestrionManager.Instance();
        var sampleState = OrchestrionSampleState.Instance();

        _newInnPlayMode = manager->PlayMode;
        _newInnMode = manager->Mode;
        _newInnPlayingTrackId = IsInnTrackPlaying() ? manager->TrackId : _oldInnPlayingTrackId;
        _newInnSamplingTrackId = IsInnTrackSampling() ? sampleState->TrackId : _oldInnSamplingTrackId;

        if (_oldInnMode != OrchestrionMode.Off && _newInnMode == OrchestrionMode.Off)
        {
            ResetNewFieldValues();
        }

        var innTrackChanged = _newInnPlayingTrackId != _oldInnPlayingTrackId;
        return innTrackChanged;
    }

    private void ResetNewFieldValues()
    {
        _newInnPlayMode = OrchestrionPlayMode.PlayAll;
        _newInnMode = OrchestrionMode.Off;
        _newInnSamplingTrackId = 0;
        _newInnPlayingTrackId = 0;
        _newInnTrackDtrName = string.Empty;
        _newInnTrackChatName = string.Empty;
    }

    private void ResetOldFieldValues()
    {
        _oldPlayMode = OrchestrionPlayMode.PlayAll;
        _oldInnMode = OrchestrionMode.Off;
        _oldInnSamplingTrackId = 0;
        _oldInnPlayingTrackId = 0;
        _oldInnTrackDtrName = string.Empty;
        _oldInnTrackChatName = string.Empty;
    }

    private void ResetAllFieldValues()
    {
        ResetOldFieldValues();
        ResetNewFieldValues();
    }

    private void RecordOldFieldValues()
    {
        _oldPlayMode = _newInnPlayMode;
        _oldInnMode = _newInnMode;
        _oldInnSamplingTrackId = _newInnSamplingTrackId;
        _oldInnPlayingTrackId = _newInnPlayingTrackId;
        _oldInnTrackDtrName = _newInnTrackDtrName;
        _oldInnTrackChatName = _newInnTrackChatName;
    }
}
