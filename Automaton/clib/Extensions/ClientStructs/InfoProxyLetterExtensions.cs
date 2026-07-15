using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Extensions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace clib.Extensions;

public static unsafe class InfoProxyLetterExtensions {
    extension(InfoProxyLetter) {
        public static bool CanTakeAttachement() => AgentLetter.Instance() is not null and var agent && agent->TransferCountdown <= 0;
        public static bool TakeAllAttachements(int index, long SenderContentId) => InfoProxyLetter.TakeAllAttachements(index, SenderContentId);
        public static bool DeleteLetter(int index) => InfoProxyLetter.Instance()->DeleteLetter((uint)index);
        public static InfoProxyLetter.Letter? MapLetter => InfoProxyLetter.Instance()->Letters.ToArray().FirstOrNull(l => l.Attachments.ToArray().Any(a => Item.GetRow(a.ItemId).FilterGroup == 18));
    }

    extension(InfoProxyLetter.Letter letter) {
        public int Index => (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(InfoProxyLetter.Instance()->Letters), ref letter) / Unsafe.SizeOf<InfoProxyLetter.Letter>());
    }
}
