using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace clib.Extensions;

public static class AtkUnitBasePtrExtensions {
    public static unsafe AtkUnitBase* Struct(this AtkUnitBasePtr wrapper) => (AtkUnitBase*)wrapper.Address;
}
