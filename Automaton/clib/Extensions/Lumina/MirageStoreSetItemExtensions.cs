using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.Interop;
using InteropGenerator.Runtime;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static unsafe class MirageStoreSetItemExtensions {
    public const int ItemCount = 11;

    extension(MirageStoreSetItem row) {
        public RowRef<Item> Set => new(row.ExcelPage.Module, row.RowId, row.ExcelPage.Language);
        public Collection<RowRef<Item>> Items => new(row.ExcelPage, parentOffset: row.RowOffset, offset: row.RowOffset, &ItemCtor, size: 11);

        // https://github.com/Haselnussbomber/HaselCommon/blob/f9ced026bb11cc36c973e6991c2c1680878277d7/HaselCommon/Services/MirageService.cs
        public bool IsSetSlotCollected(int slotIndex, bool useCache = true) {
            var mirageManager = MirageManager.Instance();
            if (mirageManager->PrismBoxLoaded) {
                var itemIndex = mirageManager->PrismBoxItemIds.IndexOf(row.RowId);
                if (itemIndex != -1) {
                    return mirageManager->IsSetSlotUnlocked((uint)itemIndex, slotIndex);
                }
            }

            if (useCache) {
                var itemFinderModule = ItemFinderModule.Instance();
                var itemIndex = itemFinderModule->GlamourDresserItemIds.IndexOf(row.RowId);
                if (itemIndex != -1) {
                    var bitArray = new BitArray((byte*)itemFinderModule->GlamourDresserItemSetUnlockBits.GetPointer(itemIndex), ItemCount);
                    return !bitArray.Get(slotIndex);
                }
            }

            return false;
        }

        public bool IsFullSetCollected(bool useCache = true) {
            var collected = true;

            foreach (var (slotIndex, slot) in row.Items.Index()) {
                if (slot.RowId == 0 || !slot.IsValid)
                    continue;

                collected &= IsSetSlotCollected(row, slotIndex, useCache);
            }

            return collected;
        }

        public void TryOnSet() => AgentTryon.Instance()->TryOnSilent(row.Items);
    }

    internal static RowRef<Item> ItemCtor(ExcelPage page, uint parentOffset, uint offset, uint i)
        => new(page.Module, page.ReadUInt32(offset + i * 4), page.Language);
}
