namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsAccessibleFromGeneratedCode(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal ||
                current.DeclaredAccessibility is Accessibility.Private or
                    Accessibility.Protected or
                    Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsRefLikeType(ITypeSymbol type)
        => type switch
        {
            INamedTypeSymbol { IsRefLikeType: true } => true,
            IArrayTypeSymbol arrayType => ContainsRefLikeType(arrayType.ElementType),
            IPointerTypeSymbol pointerType => ContainsRefLikeType(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.TypeArguments.Any(ContainsRefLikeType),
            _ => false
        };
}
