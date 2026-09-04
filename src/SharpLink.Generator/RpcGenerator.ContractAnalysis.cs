namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcInterfaceModel? GetInterfaceModelOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (!InheritsIService(symbol))
            return null;

        return HasInvalidRpcMethod(symbol) ? null : CreateInterfaceModel(symbol);
    }

    private static RpcContractDiagnosticModel? GetRpcContractDiagnosticOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (!InheritsIService(symbol))
        {
            return new RpcContractDiagnosticModel(
                RpcContractDiagnosticKind.Inheritance,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault());
        }
        if (!IsPubliclyReachableContract(symbol))
        {
            return new RpcContractDiagnosticModel(
                RpcContractDiagnosticKind.Accessibility,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault());
        }
        return null;
    }

    private static bool IsPubliclyReachableContract(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }
}
