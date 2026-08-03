namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<Dictionary<DocumentId, List<InvocationEdit>>> FindInvocationEditsAsync(
        ImmutableArray<IMethodSymbol> methods,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<DocumentId, List<InvocationEdit>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                method, solution, cancellationToken).ConfigureAwait(false);
            foreach (var location in referencedSymbols.SelectMany(static item => item.Locations))
            {
                if (!location.Location.IsInSource || location.Document is null)
                    continue;
                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var invocation = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || semanticModel?.GetOperation(invocation, cancellationToken)
                    is not IInvocationOperation operation ||
                    !methods.Any(candidate => IsSameMethod(operation.TargetMethod, candidate)))
                {
                    continue;
                }

                var key = document.Id + ":" + invocation.SpanStart.ToString(CultureInfo.InvariantCulture);
                if (!seen.Add(key))
                    continue;
                var ordinals = new Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, int>();
                foreach (var argument in operation.Arguments)
                {
                    if (!argument.IsImplicit && argument.Syntax is ArgumentSyntax syntax && argument.Parameter is { } parameter)
                        ordinals[syntax.Span] = parameter.Ordinal;
                }
                var flags = GetControlParameterFlags(operation.TargetMethod);
                if (!result.TryGetValue(document.Id, out var edits))
                {
                    edits = [];
                    result.Add(document.Id, edits);
                }
                edits.Add(new InvocationEdit(
                    invocation.Span,
                    ordinals,
                    operation.TargetMethod.Parameters.Select(static parameter => parameter.Name).ToImmutableArray(),
                    flags.CancellationTokens,
                    flags.CallOptions));
            }
        }
        return result;
    }

    private static async Task<bool> CanIntroduceNamedArgumentsAtInvocationSitesAsync(
        ImmutableArray<IMethodSymbol> methods,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var edits = await FindInvocationEditsAsync(methods, solution, cancellationToken).ConfigureAwait(false);
        foreach (var pair in edits)
        {
            var document = solution.GetDocument(pair.Key);
            if (document?.Project.ParseOptions is not CSharpParseOptions parseOptions ||
                parseOptions.LanguageVersion is LanguageVersion.Default or
                    LanguageVersion.Latest or LanguageVersion.LatestMajor or LanguageVersion.Preview ||
                parseOptions.LanguageVersion > LanguageVersion.CSharp13)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return false;
            foreach (var edit in pair.Value)
            {
                var invocation = root.FindNode(edit.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation?.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(lambda =>
                        IsExpressionTree(lambda, semanticModel, cancellationToken)) == true)
                {
                    return false;
                }

                var query = invocation?.Ancestors().OfType<QueryExpressionSyntax>().FirstOrDefault();
                if (query is not null && UsesQueryableTranslation(
                        query, semanticModel, cancellationToken))
                {
                    return false;
                }
            }
        }
        return true;

        static bool IsExpressionTree(
            AnonymousFunctionExpressionSyntax lambda,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
            => semanticModel.GetTypeInfo(lambda, cancellationToken).ConvertedType is INamedTypeSymbol
            {
                Name: "Expression",
                Arity: 1,
                ContainingNamespace: { } containingNamespace
            } &&
               string.Equals(
                   containingNamespace.ToDisplayString(),
                   "System.Linq.Expressions",
                   StringComparison.Ordinal);

        static bool UsesQueryableTranslation(
            QueryExpressionSyntax query,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
            => ContainsQueryableInvocation(semanticModel.GetOperation(query, cancellationToken));

        static bool ContainsQueryableInvocation(IOperation? operation)
        {
            if (operation is IInvocationOperation
                {
                    TargetMethod.ContainingType:
                    {
                        Name: "Queryable",
                        ContainingNamespace: { } containingNamespace
                    }
                } &&
                string.Equals(
                    containingNamespace.ToDisplayString(),
                    "System.Linq",
                    StringComparison.Ordinal))
            {
                return true;
            }

            return operation?.ChildOperations.Any(ContainsQueryableInvocation) == true;
        }
    }

    private static MethodDeclarationSyntax UpdateDeclaration(
        MethodDeclarationSyntax declaration,
        DeclarationEdit edit,
        SignatureEditPlan plan)
    {
        if (plan.Kind == SignatureEditKind.MakeInstance)
        {
            return declaration.WithModifiers(RemoveModifier(
                    declaration.Modifiers, SyntaxKind.StaticKeyword))
                .WithAdditionalAnnotations(Formatter.Annotation);
        }

        var parameters = declaration.ParameterList.Parameters;
        if (parameters.Count != edit.CancellationTokens.Length)
            return declaration;

        SeparatedSyntaxList<ParameterSyntax> updatedParameters;
        switch (plan.Kind)
        {
            case SignatureEditKind.AddCancellationToken:
                var added = SyntaxFactory.Parameter(SyntaxFactory.Identifier(plan.AddedParameterName!))
                    .WithType(SyntaxFactory.ParseTypeName("global::System.Threading.CancellationToken"));
                updatedParameters = RebuildSeparatedListPreservingTrivia(
                    parameters,
                    GetControlParameterOrder(edit).Select(ordinal => (ordinal, parameters[ordinal])),
                    added);
                break;
            case SignatureEditKind.KeepControlParameter:
                updatedParameters = RebuildSeparatedListPreservingTrivia(
                    parameters,
                    parameters.Select((parameter, ordinal) => (ordinal, parameter)).Where(item =>
                        !IsControlParameter(edit, plan.ControlKind, item.ordinal) ||
                        item.ordinal == plan.KeptOrdinal));
                break;
            case SignatureEditKind.ReorderControlParameters:
                updatedParameters = RebuildSeparatedListPreservingTrivia(
                    parameters,
                    GetControlParameterOrder(edit).Select(ordinal => (ordinal, parameters[ordinal])));
                break;
            default:
                return declaration;
        }

        return declaration.WithParameterList(declaration.ParameterList.WithParameters(updatedParameters))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax originalInvocation,
        InvocationExpressionSyntax rewrittenInvocation,
        InvocationEdit edit,
        SignatureEditPlan plan)
    {
        if (plan.Kind == SignatureEditKind.MakeInstance)
            return rewrittenInvocation;

        var originalArguments = originalInvocation.ArgumentList.Arguments;
        var arguments = rewrittenInvocation.ArgumentList.Arguments;
        if (originalArguments.Count != arguments.Count)
            return rewrittenInvocation;

        int GetArgumentOrdinal(int index)
            => edit.ArgumentOrdinals.TryGetValue(originalArguments[index].Span, out var ordinal)
                ? ordinal
                : int.MaxValue - arguments.Count + index;

        SeparatedSyntaxList<ArgumentSyntax> updatedArguments;
        switch (plan.Kind)
        {
            case SignatureEditKind.AddCancellationToken:
                var added = SyntaxFactory.Argument(
                        SyntaxFactory.ParseExpression("global::System.Threading.CancellationToken.None"))
                    .WithNameColon(CreateNameColon(plan.AddedParameterName!));
                updatedArguments = RebuildSeparatedListPreservingTrivia(
                    arguments,
                    arguments.Select((argument, index) =>
                        (index, NameArgument(argument, GetArgumentOrdinal(index), edit))),
                    added);
                break;
            case SignatureEditKind.KeepControlParameter:
                updatedArguments = RebuildSeparatedListPreservingTrivia(
                    arguments,
                    arguments.Select((argument, index) => (index, argument)).Where(item =>
                    {
                        if (!edit.ArgumentOrdinals.TryGetValue(
                                originalArguments[item.index].Span, out var ordinal))
                        {
                            return true;
                        }
                        return !IsControlParameter(edit, plan.ControlKind, ordinal) ||
                               ordinal == plan.KeptOrdinal;
                    }));
                break;
            case SignatureEditKind.ReorderControlParameters:
                updatedArguments = RebuildSeparatedListPreservingTrivia(
                    arguments,
                    arguments.Select((argument, index) =>
                        (index, NameArgument(argument, GetArgumentOrdinal(index), edit))));
                break;
            default:
                return rewrittenInvocation;
        }

        return rewrittenInvocation.WithArgumentList(
                rewrittenInvocation.ArgumentList.WithArguments(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }
}
