namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static readonly SymbolDisplayFormat DiagnosticMethodDisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)
        .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters)
        .WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeExplicitInterface |
            SymbolDisplayMemberOptions.IncludeParameters)
        .WithParameterOptions(
            SymbolDisplayParameterOptions.IncludeParamsRefOut |
            SymbolDisplayParameterOptions.IncludeType);

    private static IMethodSymbol? ResolveDiagnosticMethodSymbol(
        IMethodSymbol declaredMethod,
        Compilation compilation,
        Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.SymbolIdentity, out var identity) ||
            string.IsNullOrWhiteSpace(identity))
        {
            return declaredMethod;
        }

        foreach (var type in GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            foreach (var container in GetMethodContainers(type))
            {
                foreach (var candidate in container.GetMembers(declaredMethod.Name).OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            candidate.OriginalDefinition,
                            declaredMethod.OriginalDefinition) &&
                        string.Equals(GetDiagnosticMethodIdentity(candidate), identity, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    private static string GetDiagnosticMethodIdentity(IMethodSymbol method)
        => method.ContainingAssembly.Identity + ":" +
           method.ToDisplayString(DiagnosticMethodDisplayFormat);

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers())
        {
            foreach (var candidate in GetTypeAndNestedTypes(type))
                yield return candidate;
        }
        foreach (var child in @namespace.GetNamespaceMembers())
        {
            foreach (var candidate in GetAllTypes(child))
                yield return candidate;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetTypeAndNestedTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var candidate in GetTypeAndNestedTypes(nested))
                yield return candidate;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetMethodContainers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
        foreach (var @interface in type.AllInterfaces)
            yield return @interface;
    }
}
