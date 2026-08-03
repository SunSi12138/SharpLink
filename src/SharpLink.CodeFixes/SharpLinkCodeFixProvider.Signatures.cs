namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private enum ControlParameterKind
    {
        CancellationToken,
        CallOptions
    }

    private enum SignatureEditKind
    {
        AddCancellationToken,
        KeepControlParameter,
        ReorderControlParameters,
        MakeInstance
    }

    private sealed class SignatureEditPlan
    {
        internal SignatureEditPlan(
            SignatureEditKind kind,
            ControlParameterKind controlKind = default,
            int keptOrdinal = -1,
            string? addedParameterName = null)
        {
            Kind = kind;
            ControlKind = controlKind;
            KeptOrdinal = keptOrdinal;
            AddedParameterName = addedParameterName;
        }

        internal SignatureEditKind Kind { get; }
        internal ControlParameterKind ControlKind { get; }
        internal int KeptOrdinal { get; }
        internal string? AddedParameterName { get; }
    }

    private sealed class DeclarationEdit
    {
        internal DeclarationEdit(
            Microsoft.CodeAnalysis.Text.TextSpan span,
            ImmutableArray<bool> cancellationTokens,
            ImmutableArray<bool> callOptions)
        {
            Span = span;
            CancellationTokens = cancellationTokens;
            CallOptions = callOptions;
        }

        internal Microsoft.CodeAnalysis.Text.TextSpan Span { get; }
        internal ImmutableArray<bool> CancellationTokens { get; }
        internal ImmutableArray<bool> CallOptions { get; }
    }

    private sealed class InvocationEdit
    {
        internal InvocationEdit(
            Microsoft.CodeAnalysis.Text.TextSpan span,
            Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, int> argumentOrdinals,
            ImmutableArray<string> parameterNames,
            ImmutableArray<bool> cancellationTokens,
            ImmutableArray<bool> callOptions)
        {
            Span = span;
            ArgumentOrdinals = argumentOrdinals;
            ParameterNames = parameterNames;
            CancellationTokens = cancellationTokens;
            CallOptions = callOptions;
        }

        internal Microsoft.CodeAnalysis.Text.TextSpan Span { get; }
        internal Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, int> ArgumentOrdinals { get; }
        internal ImmutableArray<string> ParameterNames { get; }
        internal ImmutableArray<bool> CancellationTokens { get; }
        internal ImmutableArray<bool> CallOptions { get; }
    }

    private static async Task<Solution> AddCancellationTokenAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var method = await ResolveMethodSymbolAsync(solution, documentId, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (method is null)
            return solution;
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        var name = GetCollisionFreeParameterName(
            related, "cancellationToken", cancellationToken);
        return await ApplySignatureEditAsync(
            solution,
            related,
            new SignatureEditPlan(SignatureEditKind.AddCancellationToken, addedParameterName: name),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> KeepControlParameterAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        ControlParameterKind kind,
        int keptOrdinal,
        CancellationToken cancellationToken)
    {
        var method = await ResolveMethodSymbolAsync(solution, documentId, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (method is null)
            return solution;
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        return await ApplySignatureEditAsync(
            solution,
            related,
            new SignatureEditPlan(SignatureEditKind.KeepControlParameter, kind, keptOrdinal),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> ReorderControlParametersAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var method = await ResolveMethodSymbolAsync(solution, documentId, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (method is null)
            return solution;
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        return await ApplySignatureEditAsync(
            solution,
            related,
            new SignatureEditPlan(SignatureEditKind.ReorderControlParameters),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> MakeInstanceMethodAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var method = await ResolveMethodSymbolAsync(solution, documentId, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (method is null)
            return solution;
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        return await ApplySignatureEditAsync(
            solution,
            related,
            new SignatureEditPlan(SignatureEditKind.MakeInstance),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IMethodSymbol?> ResolveMethodSymbolAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        if (document is null)
            return null;
        var declaration = await FindNodeAsync<MethodDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return declaration is null ? null : semanticModel?.GetDeclaredSymbol(declaration, cancellationToken);
    }

    private static async Task<ImmutableArray<IMethodSymbol>> FindRelatedMethodsAsync(
        IMethodSymbol method,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var methods = new List<IMethodSymbol>();
        var pending = new Queue<IMethodSymbol>();
        Add(method);
        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            var equivalentInterfaceMethods = await FindEquivalentInterfaceMethodsAsync(
                method, solution, cancellationToken).ConfigureAwait(false);
            foreach (var equivalent in equivalentInterfaceMethods)
                Add(equivalent);
        }

        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            var implementations = await SymbolFinder.FindImplementationsAsync(
                current, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var implementation in implementations.OfType<IMethodSymbol>())
                Add(implementation);

            var overrides = await SymbolFinder.FindOverridesAsync(
                current, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var @override in overrides.OfType<IMethodSymbol>())
                Add(@override);

            foreach (var implemented in current.ExplicitInterfaceImplementations)
                Add(implemented);

            if (current.ContainingType.TypeKind == TypeKind.Class)
            {
                foreach (var @interface in current.ContainingType.AllInterfaces)
                {
                    foreach (var candidate in @interface.GetMembers(current.Name).OfType<IMethodSymbol>())
                    {
                        var implementation = current.ContainingType.FindImplementationForInterfaceMember(candidate);
                        if (SymbolEqualityComparer.Default.Equals(implementation, current))
                            Add(candidate);
                    }
                }
            }
        }

        return methods.ToImmutableArray();

        void Add(IMethodSymbol candidate)
        {
            if (methods.Any(item => SymbolEqualityComparer.Default.Equals(item, candidate)))
                return;
            methods.Add(candidate);
            pending.Enqueue(candidate);
        }
    }

    private static async Task<bool> CanSafelyChangeSignatureAsync(
        IMethodSymbol method,
        Solution solution,
        bool allowInvocations,
        bool allowSignatureQualifiedCrefs,
        CancellationToken cancellationToken)
    {
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        if (related.Any(candidate => !HasOnlyRegularEditableDeclarations(candidate, solution)))
            return false;
        foreach (var candidate in related)
        {
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                candidate, solution, cancellationToken).ConfigureAwait(false);
            foreach (var location in referencedSymbols.SelectMany(static item => item.Locations))
            {
                if (!location.Location.IsInSource || location.Document is null)
                    continue;
                var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var node = root?.FindNode(
                    location.Location.SourceSpan,
                    findInsideTrivia: true,
                    getInnermostNodeForTie: true);
                if (node is null)
                    continue;
                if (allowInvocations)
                {
                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    var semanticModel = invocation is null
                        ? null
                        : await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    if (invocation is not null &&
                        semanticModel?.GetOperation(invocation, cancellationToken) is IInvocationOperation operation &&
                        related.Any(relatedMethod => IsSameMethod(operation.TargetMethod, relatedMethod)))
                    {
                        if (location.Location.SourceTree is not { } invocationTree ||
                            !IsRegularEditableDocument(solution, invocationTree))
                        {
                            return false;
                        }
                        continue;
                    }
                }
                var cref = node.AncestorsAndSelf().OfType<CrefSyntax>().FirstOrDefault() ??
                           node.DescendantNodesAndSelf().OfType<CrefSyntax>().FirstOrDefault();
                if (cref is not null)
                {
                    if (!allowSignatureQualifiedCrefs &&
                        cref.DescendantTokens().Any(static token =>
                            token.IsKind(SyntaxKind.OpenParenToken) && !token.IsMissing))
                    {
                        return false;
                    }
                    continue;
                }
                if (node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any(static invocation =>
                        invocation.Expression is IdentifierNameSyntax identifier &&
                        identifier.Identifier.ValueText == "nameof"))
                {
                    continue;
                }
                return false;
            }
        }
        return true;
    }

    private static async Task<Solution> ApplySignatureEditAsync(
        Solution solution,
        ImmutableArray<IMethodSymbol> methods,
        SignatureEditPlan plan,
        CancellationToken cancellationToken)
    {
        var declarationEdits = new Dictionary<DocumentId, List<DeclarationEdit>>();
        foreach (var method in methods)
        {
            var flags = GetControlParameterFlags(method);
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                if (!declarationEdits.TryGetValue(document.Id, out var edits))
                {
                    edits = [];
                    declarationEdits.Add(document.Id, edits);
                }
                if (edits.All(item => item.Span != reference.Span))
                    edits.Add(new DeclarationEdit(reference.Span, flags.CancellationTokens, flags.CallOptions));
            }
        }

        var invocationEdits = await FindInvocationEditsAsync(methods, solution, cancellationToken)
            .ConfigureAwait(false);
        var documentIds = declarationEdits.Keys.Concat(invocationEdits.Keys).Distinct().ToArray();
        foreach (var documentId in documentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = solution.GetDocument(documentId);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (document is null || root is null)
                continue;

            var nodeEdits = new Dictionary<SyntaxNode, object>();
            if (declarationEdits.TryGetValue(documentId, out var declarations))
            {
                foreach (var edit in declarations)
                {
                    var node = root.FindNode(edit.Span, getInnermostNodeForTie: true)
                        .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (node is not null)
                        nodeEdits[node] = edit;
                }
            }
            if (invocationEdits.TryGetValue(documentId, out var invocations))
            {
                foreach (var edit in invocations)
                {
                    var node = root.FindNode(edit.Span, getInnermostNodeForTie: true)
                        .AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (node is not null)
                        nodeEdits[node] = edit;
                }
            }

            var updatedRoot = root.ReplaceNodes(nodeEdits.Keys, (original, current) =>
            {
                var edit = nodeEdits[original];
                return edit switch
                {
                    DeclarationEdit declaration => UpdateDeclaration(
                        (MethodDeclarationSyntax)current, declaration, plan),
                    InvocationEdit invocation => UpdateInvocation(
                        (InvocationExpressionSyntax)original,
                        (InvocationExpressionSyntax)current,
                        invocation,
                        plan),
                    _ => current
                };
            });
            solution = solution.WithDocumentSyntaxRoot(documentId, updatedRoot);
        }
        return solution;
    }

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
