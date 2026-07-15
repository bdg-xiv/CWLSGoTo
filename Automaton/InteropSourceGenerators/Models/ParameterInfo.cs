using Microsoft.CodeAnalysis;

namespace InteropSourceGenerators.Models;

internal sealed record ParameterInfo(
    string Name,
    string Type,
    string? DefaultValue,
    RefKind RefKind);
