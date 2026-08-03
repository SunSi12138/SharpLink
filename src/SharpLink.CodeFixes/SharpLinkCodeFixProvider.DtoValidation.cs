namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<ImmutableArray<INamedTypeSymbol>> GetDtoTypesToValidateAsync(
        INamedTypeSymbol type,
        Project project,
        CancellationToken cancellationToken)
    {
        if (!type.IsGenericType && !GetContainingTypes(type).Any(static containing => containing.IsGenericType))
            return ImmutableArray.Create(type);

        var result = new List<INamedTypeSymbol>();
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                continue;
            foreach (var syntax in root.DescendantNodes().OfType<TypeSyntax>())
            {
                if (semanticModel.GetTypeInfo(syntax, cancellationToken).Type is not INamedTypeSymbol candidate ||
                    ContainsTypeParameter(candidate) ||
                    !SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, type.OriginalDefinition) ||
                    result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                {
                    continue;
                }
                result.Add(candidate);
            }
        }
        return result.ToImmutableArray();
    }
}
