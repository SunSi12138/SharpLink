namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
            return true;

        if (type is IArrayTypeSymbol array)
            return ContainsTypeParameter(array.ElementType);

        if (type is not INamedTypeSymbol named)
            return false;

        foreach (var argument in named.TypeArguments)
        {
            if (ContainsTypeParameter(argument))
                return true;
        }

        return false;
    }
}
