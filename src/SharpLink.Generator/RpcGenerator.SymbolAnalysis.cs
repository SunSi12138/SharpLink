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
}
