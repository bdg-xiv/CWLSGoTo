using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static unsafe class EquipRaceCategoryExtensions {
    extension(EquipRaceCategory row) {
        public Collection<bool> Races
            => new(row.ExcelPage, parentOffset: row.RowOffset, offset: row.RowOffset, &RaceCtor, size: row.ExcelPage.Module.GetSheet<Race>().Count);

        public Collection<bool> Sexes
            => new(row.ExcelPage, parentOffset: row.RowOffset, offset: row.RowOffset + (uint)row.ExcelPage.Module.GetSheet<Race>().Count - 1, &SexCtor, size: 2);

        public bool CanEquip => get_Races(row)[PlayerState.Instance()->Race - 1] && get_Sexes(row)[PlayerState.Instance()->Sex]; // race rows start at 1, but the collection is 0-indexed
    }

    private static bool RaceCtor(ExcelPage page, uint parentOffset, uint offset, uint i)
        => page.ReadBool(offset + i);

    private static bool SexCtor(ExcelPage page, uint parentOffset, uint offset, uint i)
        => page.ReadPackedBool(offset, (byte)i);
}
