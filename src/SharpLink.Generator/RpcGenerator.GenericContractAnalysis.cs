namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<InvalidGenericUsageModel> GetInvalidGenericUsage(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidGenericUsageModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidGenericUsageModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidGenericUsageModel>();
        if (symbol.Arity > 0 || HasGenericContainingType(symbol))
        {
            list.Add(new InvalidGenericUsageModel(
                symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault()));
        }

        foreach (var method in GetContractMethods(symbol))
        {
            var hasGenericUsage = method.IsGenericMethod ||
                                  HasTypeParameter(method.ReturnType) ||
                                  method.Parameters.Any(p => HasTypeParameter(p.Type));
            if (!hasGenericUsage)
                continue;

            list.Add(new InvalidGenericUsageModel(
                method.Name,
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static bool HasGenericContainingType(INamedTypeSymbol symbol)
    {
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0)
                return true;
        }
        return false;
    }

    private static bool HasTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
            return true;

        return type switch
        {
            IArrayTypeSymbol arrayType => HasTypeParameter(arrayType.ElementType),
            IPointerTypeSymbol pointerType => HasTypeParameter(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.IsUnboundGenericType ||
                                         namedType.TypeArguments.Any(HasTypeParameter),
            _ => false
        };
    }
}
