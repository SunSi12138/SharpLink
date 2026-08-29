namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static readonly SymbolDisplayFormat ClrTypeIdentityFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    private static string GetTypeName(ITypeSymbol type)
        => type.ToDisplayString(ClrTypeIdentityFormat);

    private static bool IsFrameworkWirePrimitive(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum || type.SpecialType == SpecialType.System_String)
            return true;

        if (type is IArrayTypeSymbol
            {
                Rank: 1,
                ElementType.SpecialType: SpecialType.System_Byte
            })
        {
            return true;
        }

        if (type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Char or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Single or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Double or
            SpecialType.System_Decimal)
        {
            return true;
        }

        return type.ToDisplayString() is
            "System.Half" or
            "System.Text.Rune" or
            "System.Guid" or
            "System.DateTimeOffset" or
            "System.DateTime" or
            "System.DateOnly" or
            "System.TimeOnly" or
            "System.TimeSpan" or
            "System.Int128" or
            "System.UInt128" or
            "System.Index" or
            "System.Range";
    }

    private sealed partial class DtoAnalysisState
    {
        private void Report(
            DtoDiagnosticKind kind,
            ISymbol symbol,
            string detail,
            Location? location = null)
        {
            switch (symbol)
            {
                case ITypeSymbol type:
                    Report(kind, type, detail, location);
                    break;
                case IAssemblySymbol assembly:
                    Report(kind, assembly, detail, location);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Codec policy diagnostic owner '{symbol.Kind}'.");
            }
        }
    }
}
