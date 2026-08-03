namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<bool> CanRemoveNonCancellableFromAllConstructionsAsync(
        ImmutableArray<IMethodSymbol> attributedMethods,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var originalMethodIdentities = new HashSet<string>(
            attributedMethods.Select(GetOriginalMethodIdentity),
            StringComparer.Ordinal);
        var foundConstruction = false;
        foreach (var project in solution.Projects.Where(static project => project.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return false;

            foreach (var type in GetAllTypes(compilation.Assembly.GlobalNamespace))
            {
                foreach (var container in GetMethodContainers(type))
                {
                    if (ContainsTypeParameter(container))
                        continue;
                    foreach (var candidate in container.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (!originalMethodIdentities.Contains(GetOriginalMethodIdentity(candidate)))
                            continue;
                        foundConstruction = true;
                        if (!candidate.Parameters.Any(parameter =>
                                IsControlParameter(parameter, ControlParameterKind.CancellationToken)))
                        {
                            return false;
                        }
                    }
                }
            }
        }
        return foundConstruction;
    }

    private static string GetOriginalMethodIdentity(IMethodSymbol method)
        => method.OriginalDefinition.ContainingAssembly.Identity + ":" +
           method.OriginalDefinition.ToDisplayString(DiagnosticMethodDisplayFormat);
}
