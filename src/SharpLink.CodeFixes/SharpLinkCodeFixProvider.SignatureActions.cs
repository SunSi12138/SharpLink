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
        if (method is null ||
            IsObsoleteWithError(method))
        {
            return;
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var attributedMethods = equivalentMethods.Where(HasNonCancellableAttribute).ToImmutableArray();
        var attributes = attributedMethods.SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsNonCancellableAttribute).ToImmutableArray();
        if (attributes.Length == 0 || attributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
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

    private static async Task RegisterKeepParameterFixesAsync(
        CodeFixContext context,
        Diagnostic diagnostic,
        ControlParameterKind kind)
    {
        var symbol = await ResolveMethodSymbolAsync(
            context.Document.Project.Solution,
            context.Document.Id,
            diagnostic,
            context.CancellationToken).ConfigureAwait(false);
        if (symbol is null || IsObsoleteWithError(symbol))
            return;
        if (!await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        var relatedMethods = await FindRelatedMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        foreach (var parameter in symbol.Parameters.Where(parameter => IsControlParameter(parameter, kind)))
        {
            var ordinal = parameter.Ordinal;
            if (!CanApplySignatureEditWithoutCollisions(
                    relatedMethods,
                    new SignatureEditPlan(SignatureEditKind.KeepControlParameter, kind, ordinal)) ||
                !CanRemoveControlParametersWithoutBreakingNameReferences(relatedMethods, kind, ordinal) ||
                !await CanRemoveControlArgumentsWithoutSideEffectsAsync(
                        relatedMethods,
                        kind,
                        ordinal,
                        context.Document.Project.Solution,
                        context.CancellationToken)
                    .ConfigureAwait(false))
            {
                continue;
            }
            var displayKind = kind == ControlParameterKind.CancellationToken
                ? "CancellationToken"
                : "SharpLinkCallOptions";
            RegisterSolutionFix(
                context,
                diagnostic,
                $"Keep {displayKind} '{parameter.Name}'",
                SignatureKeyPrefix + "Keep:" + kind + ":" + ordinal.ToString(CultureInfo.InvariantCulture),
                (solution, documentId, item, ct) => KeepControlParameterAsync(
                    solution, documentId, item, kind, ordinal, ct));
        }
    }

    private static async Task RegisterReorderControlParametersFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var symbol = await ResolveMethodSymbolAsync(
            context.Document.Project.Solution,
            context.Document.Id,
            diagnostic,
            context.CancellationToken).ConfigureAwait(false);
        if (symbol is null ||
            IsObsoleteWithError(symbol) ||
            symbol.Parameters.Count(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CancellationToken)) > 1 ||
            symbol.Parameters.Count(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CallOptions)) > 1 ||
            !await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var relatedMethods = await FindRelatedMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        if (relatedMethods.Any(static related =>
                related.Parameters.Any(static parameter => parameter.IsOptional || parameter.IsParams)) ||
            !CanApplySignatureEditWithoutCollisions(
                relatedMethods,
                new SignatureEditPlan(SignatureEditKind.ReorderControlParameters)) ||
            !CanReorderControlParametersWithoutBreakingHandlerDependencies(relatedMethods) ||
            !await CanIntroduceNamedArgumentsAtInvocationSitesAsync(
                    relatedMethods,
                    context.Document.Project.Solution,
                    context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, "Reorder RPC control parameters",
            SignatureKeyPrefix + "ReorderControlParameters", ReorderControlParametersAsync);
    }
}
