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
                if (query is not null && UsesExpressionTreeTranslation(
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
            => IsExpressionTreeType(semanticModel.GetTypeInfo(lambda, cancellationToken).ConvertedType);

        static bool IsExpressionTreeType(ITypeSymbol? type)
            => type is INamedTypeSymbol
            {
                Name: "Expression",
                Arity: 1,
                ContainingNamespace: { } containingNamespace
            } &&
               string.Equals(
                   containingNamespace.ToDisplayString(),
                   "System.Linq.Expressions",
                   StringComparison.Ordinal);

        static bool UsesExpressionTreeTranslation(
            QueryExpressionSyntax query,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
            => ContainsExpressionTreeArgument(semanticModel.GetOperation(query, cancellationToken));

        static bool ContainsExpressionTreeArgument(IOperation? operation)
        {
            if (operation is IArgumentOperation argument &&
                (IsExpressionTreeType(argument.Parameter?.Type) ||
                 IsExpressionTreeType(argument.Value.Type)))
            {
                return true;
            }

            return operation?.ChildOperations.Any(ContainsExpressionTreeArgument) == true;
        }
    }

    private static async Task<bool> CanRemoveControlArgumentsWithoutSideEffectsAsync(
        ImmutableArray<IMethodSymbol> methods,
        ControlParameterKind kind,
        int keptOrdinal,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var edits = await FindInvocationEditsAsync(methods, solution, cancellationToken).ConfigureAwait(false);
        foreach (var pair in edits)
        {
            var document = solution.GetDocument(pair.Key);
            var root = document is null
                ? null
                : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = document is null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null)
                return false;

            foreach (var edit in pair.Value)
            {
                var invocation = root.FindNode(edit.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null)
                    return false;

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    if (!edit.ArgumentOrdinals.TryGetValue(argument.Span, out var ordinal) ||
                        ordinal == keptOrdinal ||
                        !IsControlParameter(edit, kind, ordinal))
                    {
                        continue;
                    }

                    if (!CanDiscardWithoutObservableSideEffects(
                            semanticModel.GetOperation(argument.Expression, cancellationToken)))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static bool CanDiscardWithoutObservableSideEffects(IOperation? operation)
        => operation switch
        {
            IDefaultValueOperation => true,
            ILiteralOperation => true,
            ILocalReferenceOperation => true,
            IParameterReferenceOperation => true,
            IConversionOperation conversion => CanDiscardWithoutObservableSideEffects(conversion.Operand),
            IParenthesizedOperation parenthesized =>
                CanDiscardWithoutObservableSideEffects(parenthesized.Operand),
            IPropertyReferenceOperation
            {
                Property:
                {
                    Name: "None",
                    IsStatic: true,
                    ContainingType:
                    {
                        Name: "CancellationToken",
                        ContainingNamespace: { } containingNamespace
                    }
                }
            } => string.Equals(
                containingNamespace.ToDisplayString(),
                "System.Threading",
                StringComparison.Ordinal),
            _ => false
        };

    private static MethodDeclarationSyntax UpdateDeclaration(
        MethodDeclarationSyntax declaration,
        DeclarationEdit edit,
        SignatureEditPlan plan)
    {
        if (plan.Kind == SignatureEditKind.MakeInstance)
        {
            return RemoveModifier(declaration, SyntaxKind.StaticKeyword)
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
