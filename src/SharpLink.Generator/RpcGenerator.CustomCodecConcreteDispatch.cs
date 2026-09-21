namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private static bool SupportsDirectCustomCodecDispatch(
            INamedTypeSymbol codecType,
            ITypeSymbol targetType)
        {
            var codecInterface = codecType.AllInterfaces.FirstOrDefault(item =>
                item is
                {
                    Name: "IRpcCodec",
                    Arity: 1
                } &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                SymbolEqualityComparer.Default.Equals(item.TypeArguments[0], targetType));
            if (codecInterface is null)
                return false;

            foreach (var member in codecInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (codecType.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation ||
                    implementation.DeclaredAccessibility != Accessibility.Public)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
