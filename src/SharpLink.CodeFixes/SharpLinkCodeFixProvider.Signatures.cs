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

}
