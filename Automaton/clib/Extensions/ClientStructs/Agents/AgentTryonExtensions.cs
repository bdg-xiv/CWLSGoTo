using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static unsafe class AgentTryonExtensions {
    extension(ref AgentTryon agent) {
        public void TryOnSilent(RowRef<Item> item) => agent.TryOnSilent(item.RowId);
        public void TryOnSilent(Item item) => agent.TryOnSilent(item.RowId);
        public void TryOnSilent(Collection<RowRef<Item>> items) => agent.TryOnSilent(items.Select(r => r.RowId).ToArray());
        public void TryOnSilent(uint itemId) => agent.TryOnSilent([itemId]);
        public void TryOnSilent(uint[] itemIds) {
            var agentColorant = AgentColorant.Instance();
            if (agentColorant->IsAgentActive())
                agentColorant->Hide();

            UIModule.Instance()->GetAgentHelpers()->HideBlockingCharaViewAgents(2, AgentId.Tryon);

            agent.TryOnItems.Clear();

            foreach (ref var item in agent.TryOnItems)
                item.EquipSlotCategory = 0xE; // sets slot to an empty state

            var i = 0;
            foreach (var item in itemIds)
                agent.TryOnItems[i++].Id = item;

            agent.TryOnItemsChanged = true;

            if (!agent.IsAgentActive())
                agent.Show();
        }
    }
}
