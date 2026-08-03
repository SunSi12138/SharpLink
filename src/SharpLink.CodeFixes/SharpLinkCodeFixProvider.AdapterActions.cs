namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task RegisterAdapterShapeFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null || semanticModel is null)
            return;

        var adapterArgument = attribute.ArgumentList?.Arguments.FirstOrDefault(argument =>
            semanticModel.GetOperation(argument, context.CancellationToken) is
                IArgumentOperation { Parameter.Name: "adapterType" });
        var adapterTypeSyntax = (adapterArgument?.Expression as TypeOfExpressionSyntax)?.Type;
        if (adapterTypeSyntax is null ||
            semanticModel.GetTypeInfo(adapterTypeSyntax, context.CancellationToken).Type is not INamedTypeSymbol adapter ||
            !adapter.AllInterfaces.Any(IsCodecAdapter) ||
            adapter.TypeKind != TypeKind.Class || adapter.IsGenericType ||
            GetContainingTypes(adapter).Any(static item => item.IsGenericType) ||
            (adapter.IsAbstract && !IsSafeToMakeConcrete(adapter)) ||
            HasMembersIncompatibleWithSealing(adapter, allowParameterlessConstructorPublicization: true) ||
            !CanExposePublicParameterlessConstructor(adapter) ||
            !CanCallParameterlessConstructorWithRequiredMembers(adapter) ||
            HasPrimaryConstructorWithoutParameterlessAlternative(adapter, context.CancellationToken) ||
            !TryGetPublicizationClosure(adapter, context.Document.Project.Solution, out _) ||
            adapter.DeclaringSyntaxReferences.Length == 0 ||
            adapter.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(context.CancellationToken) is not ClassDeclarationSyntax))
        {
            return;
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            adapter,
            context.Document.Project.Solution,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        if (derivedTypes.Any(static type => type.Locations.Any(static location => location.IsInSource)))
            return;

        RegisterSolutionFix(context, diagnostic, $"Fix {adapter.Name} Codec adapter shape", "FixAdapterShape",
            (solution, _, _, ct) => FixAdapterShapeAsync(solution, adapter, ct));
    }
}
