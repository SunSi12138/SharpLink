namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static readonly SymbolDisplayFormat ClrTypeIdentityFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    private static string GetTypeName(ITypeSymbol type)
        => type.ToDisplayString(ClrTypeIdentityFormat);

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
