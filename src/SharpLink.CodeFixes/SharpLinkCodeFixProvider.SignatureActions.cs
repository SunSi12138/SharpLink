namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
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
