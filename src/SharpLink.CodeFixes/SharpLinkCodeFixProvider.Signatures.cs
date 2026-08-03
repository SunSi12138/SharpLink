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
            ImmutableArray<bool> cancellationTokens,
            ImmutableArray<bool> callOptions)
        {
            Span = span;
            ArgumentOrdinals = argumentOrdinals;
            CancellationTokens = cancellationTokens;
            CallOptions = callOptions;
        }

        internal Microsoft.CodeAnalysis.Text.TextSpan Span { get; }
        internal Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, int> ArgumentOrdinals { get; }
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
        var name = GetCollisionFreeParameterName(related, "cancellationToken");
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

        return methods
            .Where(static item => item.DeclaringSyntaxReferences.Length != 0)
            .ToImmutableArray();

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
        CancellationToken cancellationToken)
    {
        var related = await FindRelatedMethodsAsync(method, solution, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in related)
        {
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                candidate, solution, cancellationToken).ConfigureAwait(false);
            foreach (var location in referencedSymbols.SelectMany(static item => item.Locations))
            {
                if (!location.Location.IsInSource || location.Document is null)
                    continue;
                var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var node = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null)
                    continue;
                if (allowInvocations && node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any())
                    continue;
                if (node.AncestorsAndSelf().Any(static item => item is CrefSyntax) ||
                    node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any(static invocation =>
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
                        (InvocationExpressionSyntax)current, invocation, plan),
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
                    is not IInvocationOperation operation)
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
                    invocation.Span, ordinals, flags.CancellationTokens, flags.CallOptions));
            }
        }
        return result;
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
                updatedParameters = SyntaxFactory.SeparatedList(GetControlParameterOrder(edit)
                    .Select(ordinal => parameters[ordinal]))
                    .Add(added);
                break;
            case SignatureEditKind.KeepControlParameter:
                updatedParameters = SyntaxFactory.SeparatedList(parameters.Where((_, ordinal) =>
                    !IsControlParameter(edit, plan.ControlKind, ordinal) || ordinal == plan.KeptOrdinal));
                break;
            case SignatureEditKind.ReorderControlParameters:
                updatedParameters = SyntaxFactory.SeparatedList(GetControlParameterOrder(edit)
                    .Select(ordinal => parameters[ordinal]));
                break;
            default:
                return declaration;
        }

        return declaration.WithParameterList(declaration.ParameterList.WithParameters(updatedParameters))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static InvocationExpressionSyntax UpdateInvocation(
        InvocationExpressionSyntax invocation,
        InvocationEdit edit,
        SignatureEditPlan plan)
    {
        if (plan.Kind == SignatureEditKind.MakeInstance)
            return invocation;

        var arguments = invocation.ArgumentList.Arguments;
        SeparatedSyntaxList<ArgumentSyntax> updatedArguments;
        switch (plan.Kind)
        {
            case SignatureEditKind.AddCancellationToken:
                var added = SyntaxFactory.Argument(
                        SyntaxFactory.ParseExpression("global::System.Threading.CancellationToken.None"))
                    .WithNameColon(SyntaxFactory.NameColon(plan.AddedParameterName!));
                var addOrder = GetControlParameterOrder(edit)
                    .Select((ordinal, index) => (ordinal, index))
                    .ToDictionary(static item => item.ordinal, static item => item.index);
                updatedArguments = SyntaxFactory.SeparatedList(arguments
                        .Select((argument, index) =>
                        {
                            var ordinal = edit.ArgumentOrdinals.TryGetValue(argument.Span, out var parameterOrdinal)
                                ? parameterOrdinal
                                : int.MaxValue - arguments.Count + index;
                            return (argument, ordinal);
                        })
                        .OrderBy(item => addOrder.TryGetValue(item.ordinal, out var index) ? index : int.MaxValue)
                        .Select(static item => item.argument))
                    .Add(added);
                break;
            case SignatureEditKind.KeepControlParameter:
                updatedArguments = SyntaxFactory.SeparatedList(arguments.Where(argument =>
                    !edit.ArgumentOrdinals.TryGetValue(argument.Span, out var ordinal) ||
                    !IsControlParameter(edit, plan.ControlKind, ordinal) ||
                    ordinal == plan.KeptOrdinal));
                break;
            case SignatureEditKind.ReorderControlParameters:
                var order = GetControlParameterOrder(edit)
                    .Select((ordinal, index) => (ordinal, index))
                    .ToDictionary(static item => item.ordinal, static item => item.index);
                updatedArguments = SyntaxFactory.SeparatedList(arguments
                    .Select((argument, index) =>
                    {
                        var ordinal = edit.ArgumentOrdinals.TryGetValue(argument.Span, out var parameterOrdinal)
                            ? parameterOrdinal
                            : int.MaxValue - arguments.Count + index;
                        return (argument, ordinal);
                    })
                    .OrderBy(item => order.TryGetValue(item.ordinal, out var index) ? index : int.MaxValue)
                    .Select(static item => item.argument));
                break;
            default:
                return invocation;
        }

        return invocation.WithArgumentList(invocation.ArgumentList.WithArguments(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static ImmutableArray<int> GetControlParameterOrder(DeclarationEdit edit)
        => GetControlParameterOrder(edit.CancellationTokens, edit.CallOptions);

    private static ImmutableArray<int> GetControlParameterOrder(InvocationEdit edit)
        => GetControlParameterOrder(edit.CancellationTokens, edit.CallOptions);

    private static ImmutableArray<int> GetControlParameterOrder(
        ImmutableArray<bool> cancellationTokens,
        ImmutableArray<bool> callOptions)
    {
        var builder = ImmutableArray.CreateBuilder<int>(cancellationTokens.Length);
        for (var index = 0; index < cancellationTokens.Length; index++)
        {
            if (!cancellationTokens[index] && !callOptions[index])
                builder.Add(index);
        }
        for (var index = 0; index < callOptions.Length; index++)
        {
            if (callOptions[index])
                builder.Add(index);
        }
        for (var index = 0; index < cancellationTokens.Length; index++)
        {
            if (cancellationTokens[index])
                builder.Add(index);
        }
        return builder.ToImmutable();
    }

    private static bool IsControlParameter(
        DeclarationEdit edit,
        ControlParameterKind kind,
        int ordinal)
        => kind == ControlParameterKind.CancellationToken
            ? edit.CancellationTokens[ordinal]
            : edit.CallOptions[ordinal];

    private static bool IsControlParameter(
        InvocationEdit edit,
        ControlParameterKind kind,
        int ordinal)
        => ordinal >= 0 && ordinal < edit.CancellationTokens.Length &&
           (kind == ControlParameterKind.CancellationToken
               ? edit.CancellationTokens[ordinal]
               : edit.CallOptions[ordinal]);

    private static bool IsControlParameter(IParameterSymbol parameter, ControlParameterKind kind)
        => kind == ControlParameterKind.CancellationToken
            ? parameter.Type.Name == "CancellationToken" &&
              parameter.Type.ContainingNamespace.ToDisplayString() == "System.Threading"
            : parameter.Type.Name == "SharpLinkCallOptions" &&
              parameter.Type.ContainingNamespace.ToDisplayString() == "SharpLink.Sdk";

    private static (ImmutableArray<bool> CancellationTokens, ImmutableArray<bool> CallOptions)
        GetControlParameterFlags(IMethodSymbol method)
        => (
            method.Parameters.Select(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CancellationToken)).ToImmutableArray(),
            method.Parameters.Select(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CallOptions)).ToImmutableArray());

    private static string GetCollisionFreeParameterName(
        ImmutableArray<IMethodSymbol> methods,
        string baseName)
    {
        var names = new HashSet<string>(
            methods.SelectMany(static item => item.Parameters).Select(static item => item.Name),
            StringComparer.Ordinal);
        if (!names.Contains(baseName))
            return baseName;
        for (var suffix = 1; ; suffix++)
        {
            var candidate = baseName + suffix.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(candidate))
                return candidate;
        }
    }
}
