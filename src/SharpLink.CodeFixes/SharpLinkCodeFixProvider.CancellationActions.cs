namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task RegisterCancellationContractFixesAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var symbol = await ResolveMethodSymbolAsync(
            context.Document.Project.Solution,
            context.Document.Id,
            diagnostic,
            context.CancellationToken).ConfigureAwait(false);
        if (symbol is null || IsObsoleteWithError(symbol))
            return;

        var canAddCancellationToken = await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
            .ConfigureAwait(false);
        if (canAddCancellationToken)
        {
            var relatedMethods = await FindRelatedMethodsAsync(
                symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
            canAddCancellationToken = !relatedMethods.Any(HasNonCancellableAttribute) &&
                                      !relatedMethods.Any(static related =>
                                          related.Parameters.Any(static parameter =>
                                              parameter.IsOptional || parameter.IsParams)) &&
                                      CanApplySignatureEditWithoutCollisions(
                                          relatedMethods,
                                          new SignatureEditPlan(SignatureEditKind.AddCancellationToken)) &&
                                      CanReorderControlParametersWithoutBreakingHandlerDependencies(relatedMethods) &&
                                      await CanIntroduceNamedArgumentsAtInvocationSitesAsync(
                                              relatedMethods,
                                              context.Document.Project.Solution,
                                              context.CancellationToken)
                                          .ConfigureAwait(false);
        }

        if (canAddCancellationToken)
        {
            RegisterSolutionFix(context, diagnostic, "Add CancellationToken",
                SignatureKeyPrefix + "AddCancellationToken", AddCancellationTokenAsync);
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        if (equivalentMethods.Length != 0 &&
            equivalentMethods.All(candidate => HasOnlyRegularEditableDeclarations(
                candidate, context.Document.Project.Solution)))
        {
            RegisterSolutionFix(context, diagnostic, "Annotate with [NonCancellable]", "AddNonCancellable",
                (solution, _, _, ct) => AddNonCancellableAsync(solution, equivalentMethods, ct));
        }
    }

    private static async Task RegisterRemoveNonCancellableFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var method = await ResolveMethodSymbolAsync(
            context.Document.Project.Solution,
            context.Document.Id,
            diagnostic,
            context.CancellationToken).ConfigureAwait(false);
        if (method is null || IsObsoleteWithError(method))
            return;

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var attributedMethods = equivalentMethods.Where(HasNonCancellableAttribute).ToImmutableArray();
        var attributes = attributedMethods.SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsNonCancellableAttribute).ToImmutableArray();
        if (attributes.Length == 0 ||
            attributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)) ||
            !await CanRemoveNonCancellableFromAllConstructionsAsync(
                    attributedMethods,
                    context.Document.Project.Solution,
                    context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [NonCancellable]",
            "RemoveNonCancellable",
            (solution, _, _, ct) => RemoveAttributesAsync(
                solution,
                attributes.Select(static attribute => attribute.ApplicationSyntaxReference!).ToImmutableArray(),
                ct));
    }
}
