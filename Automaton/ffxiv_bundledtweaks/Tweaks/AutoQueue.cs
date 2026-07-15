using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ComplexTweaks.Tweaks;

[Tweak]
internal class AutoQueue : Tweak {
    public override string Name => "Auto Queue";
    public override string Description => "Auto queue into a pre-checked duty (on zone change).\n" +
        "If in a party, waits for all players to be in the overworld, and either targetable or in another zone from you.";

    public override void Enable() => Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    public override void Disable() => Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

    private void OnTerritoryChanged(uint obj) {
        if (Player.IsInDuty || Player.IsPenalised) return;
        TaskManager.Enqueue(() => !Player.IsBusy);
        TaskManager.Enqueue(() => Svc.Party.All(p => !p.Territory.Value.IsDuty), "WaitForPartyNotInDuty");
        TaskManager.Enqueue(Svc.Condition.CanQueue, "WaitForQueueCondition");
        TaskManager.Enqueue(QueueSelectedDuty);
    }

    private unsafe bool QueueSelectedDuty() {
        var content = AgentContentsFinder.Instance()->SelectedContent;
        if (content.FirstOrNull(x => x.ContentType is ContentsType.Roulette) is { Id: var id }) {
            ContentsFinder.Instance()->QueueInfo.QueueRoulette((byte)id);
            return true;
        }
        else {
            var ids = content.Select(x => x.Id).ToList();
            var array = stackalloc uint[ids.Count];
            for (var i = 0; i < ids.Count; i++)
                array[i] = ids[i];
            ContentsFinder.Instance()->QueueInfo.QueueDuties(array, ids.Count);
            return true;
        }
    }
}
