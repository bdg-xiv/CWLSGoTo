namespace clib;

[Flags]
public enum CLibModule {
    None = 0,
    Armoire = 1 << 0,
    Automation = 1 << 1,
    SheetManager = 1 << 2,
    All = Armoire | Automation | SheetManager,
}
