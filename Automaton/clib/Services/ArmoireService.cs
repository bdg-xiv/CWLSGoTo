using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
namespace clib.Services;

public sealed unsafe class ArmoireService : IDisposable {
    public event Action? ArmoireChanged;

    private Dictionary<uint, Sheets.Cabinet> _cabinetByItemId = [];
    private readonly HashSet<uint> _ownedItemIds = [];

    public ArmoireService() {
        LoadReverseCabinetMap();

        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "Cabinet", OnCabinetRefresh);

        if (Svc.ClientState.IsLoggedIn)
            RefreshCache();
    }

    public void Dispose() {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "Cabinet", OnCabinetRefresh);
        Svc.ClientState.Logout -= OnLogout;
        Svc.ClientState.Login -= OnLogin;

        _ownedItemIds.Clear();
    }

    public void RefreshCache() {
        if (!LoadReverseCabinetMap()) {
            Svc.Log.Debug($"[{nameof(ArmoireService)}] Refreshing cabinet.");
            GameMain.ExecuteCommand(423);
        }
        BuildCache(notify: true);
    }

    public HashSet<uint> GetArmoireItems() {
        BuildCache(notify: false);
        return [.. _ownedItemIds];
    }
    public Sheets.Cabinet? GetCabinetRow(uint itemId) {
        LoadReverseCabinetMap();
        return _cabinetByItemId.TryGetValue(itemId, out var row) ? row : null;
    }

    private void OnLogin() {
        Svc.Log.Debug($"[{nameof(ArmoireService)}] Refreshing cabinet.");
        GameMain.ExecuteCommand(423);
        RefreshCache();
    }

    private void OnLogout(int _, int __) => ClearCache();

    private void ClearCache() {
        var hadAny = _ownedItemIds.Count > 0;
        _ownedItemIds.Clear();
        if (hadAny)
            ArmoireChanged?.Invoke();
    }

    private void OnCabinetRefresh(AddonEvent _, AddonArgs __) => BuildCache(notify: true);

    private void BuildCache(bool notify) {
        if (!Svc.ClientState.IsLoggedIn) {
            ClearCache();
            return;
        }

        var uiState = UIState.Instance();
        if (uiState is null || !uiState->Cabinet.IsCabinetLoaded())
            return;

        var nextOwned = new HashSet<uint>();
        foreach (var (itemId, cabinetRow) in _cabinetByItemId) {
            if (uiState->Cabinet.IsItemInCabinet(cabinetRow.RowId))
                nextOwned.Add(itemId);
        }

        var changed = !_ownedItemIds.SetEquals(nextOwned);
        _ownedItemIds.Clear();
        _ownedItemIds.UnionWith(nextOwned);

        if (notify && changed) {
            Svc.Log.Debug($"[{nameof(ArmoireService)}] Cabinet changed.");
            ArmoireChanged?.Invoke();
        }
    }

    private bool LoadReverseCabinetMap() {
        if (_cabinetByItemId.Count > 0)
            return false;

        _cabinetByItemId = Sheets.Cabinet
            .Where(x => x.RowId > 0 && x.Item.RowId > 0)
            .GroupBy(x => x.Item.RowId)
            .ToDictionary(g => g.Key, g => g.First());
        return true;
    }
}
