using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class AetheryteExtensions {
    extension(Aetheryte row) {
        public unsafe bool IsUnlocked => UIState.Instance()->IsAetheryteUnlocked(row.RowId);
    }
}
