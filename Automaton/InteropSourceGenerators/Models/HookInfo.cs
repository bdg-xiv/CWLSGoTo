namespace InteropSourceGenerators.Models;

internal sealed record HookInfo(
    ClassInfo ClassInfo,
    MethodInfo MethodInfo,
    string? AddressName = null,
    string? DelegateTypeName = null);
