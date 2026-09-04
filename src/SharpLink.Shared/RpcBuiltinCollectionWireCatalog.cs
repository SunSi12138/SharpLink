namespace SharpLink;

internal enum RpcBuiltinCollectionWireStrategy
{
    RawBlit,
    DateTimeOffsetCanonical
}

internal readonly record struct RpcBuiltinCollectionWireDescriptor(
    string ElementTypeName,
    RpcBuiltinCollectionWireStrategy Strategy,
    string Semantic);

internal static class RpcBuiltinCollectionWireCatalog
{
    internal const string RawBlitSemantic = "builtin-blit-element/v2|abi:little-endian";
    internal const string DateTimeOffsetCanonicalSemantic =
        "datetime-offset/collection16/i16le-offset-minutes/zero6/i64le-utc-ticks/v2";

    private static readonly RpcBuiltinCollectionWireDescriptor[] Items =
    {
        Raw("System.Boolean"),
        Raw("System.Byte"),
        Raw("System.SByte"),
        Raw("System.Int16"),
        Raw("System.UInt16"),
        Raw("System.Char"),
        Raw("System.Half"),
        Raw("System.Int32"),
        Raw("System.UInt32"),
        Raw("System.Single"),
        Raw("System.Text.Rune"),
        Raw("System.Int64"),
        Raw("System.UInt64"),
        Raw("System.Double"),
        Raw("System.Guid"),
        Raw("System.Decimal"),
        new("System.DateTimeOffset", RpcBuiltinCollectionWireStrategy.DateTimeOffsetCanonical,
            DateTimeOffsetCanonicalSemantic),
        Raw("System.DateTime"),
        Raw("System.DateOnly"),
        Raw("System.TimeOnly"),
        Raw("System.TimeSpan"),
        Raw("System.Int128"),
        Raw("System.UInt128"),
        Raw("System.Index"),
        Raw("System.Range")
    };

    internal static System.Collections.Generic.IReadOnlyList<RpcBuiltinCollectionWireDescriptor> All => Items;

    internal static bool TryGet(
        string elementTypeName,
        out RpcBuiltinCollectionWireDescriptor descriptor)
    {
        elementTypeName = NormalizeTypeName(elementTypeName);
        for (var index = 0; index < Items.Length; index++)
        {
            if (string.Equals(Items[index].ElementTypeName, elementTypeName, System.StringComparison.Ordinal))
            {
                descriptor = Items[index];
                return true;
            }
        }

        descriptor = default;
        return false;
    }

    private static string NormalizeTypeName(string typeName)
    {
        const string globalPrefix = "global::";
        if (typeName.StartsWith(globalPrefix, System.StringComparison.Ordinal))
            typeName = typeName.Substring(globalPrefix.Length);
        return typeName switch
        {
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "char" => "System.Char",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "float" => "System.Single",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "double" => "System.Double",
            "decimal" => "System.Decimal",
            _ => typeName
        };
    }

    private static RpcBuiltinCollectionWireDescriptor Raw(string typeName)
        => new(typeName, RpcBuiltinCollectionWireStrategy.RawBlit, RawBlitSemantic);
}
