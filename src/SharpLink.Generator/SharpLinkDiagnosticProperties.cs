namespace SharpLink.Generator;

internal static class SharpLinkDiagnosticProperties
{
    internal const string FixKind = "SharpLink.FixKind";
    internal const string SymbolIdentity = "SharpLink.SymbolIdentity";
    internal const string PreviousMemberId = "SharpLink.PreviousMemberId";
    internal const string PreviousEnumUnderlyingType = "SharpLink.PreviousEnumUnderlyingType";
    internal const string PreviousUnionTag = "SharpLink.PreviousUnionTag";
    internal const string PreviousUnionType = "SharpLink.PreviousUnionType";

    internal static ImmutableDictionary<string, string?> Create(string key, string? value)
        => ImmutableDictionary<string, string?>.Empty.Add(key, value);

    internal static ImmutableDictionary<string, string?> Create(
        string firstKey,
        string? firstValue,
        string secondKey,
        string? secondValue)
        => ImmutableDictionary<string, string?>.Empty
            .Add(firstKey, firstValue)
            .Add(secondKey, secondValue);
}
