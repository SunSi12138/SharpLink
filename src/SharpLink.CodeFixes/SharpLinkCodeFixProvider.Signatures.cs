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
        if (related.Any(static candidate => candidate.DeclaringSyntaxReferences.Length == 0))
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
                updatedArguments = SyntaxFactory.SeparatedList(arguments
                        .Select((argument, index) => NameArgument(argument, GetArgumentOrdinal(index), edit)))
                    .Add(added);
                break;
            case SignatureEditKind.KeepControlParameter:
                updatedArguments = SyntaxFactory.SeparatedList(arguments.Where((_, index) =>
                {
                    if (!edit.ArgumentOrdinals.TryGetValue(originalArguments[index].Span, out var ordinal))
                        return true;
                    return !IsControlParameter(edit, plan.ControlKind, ordinal) ||
                           ordinal == plan.KeptOrdinal;
                }));
                break;
            case SignatureEditKind.ReorderControlParameters:
                updatedArguments = SyntaxFactory.SeparatedList(arguments
                    .Select((argument, index) => NameArgument(argument, GetArgumentOrdinal(index), edit)));
                break;
            default:
                return rewrittenInvocation;
        }

        return rewrittenInvocation.WithArgumentList(
                rewrittenInvocation.ArgumentList.WithArguments(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static ArgumentSyntax NameArgument(
        ArgumentSyntax argument,
        int ordinal,
        InvocationEdit edit)
        => ordinal >= 0 && ordinal < edit.ParameterNames.Length
            ? argument.WithNameColon(CreateNameColon(edit.ParameterNames[ordinal]))
            : argument;

    private static NameColonSyntax CreateNameColon(string parameterName)
    {
        var escaped = SyntaxFacts.GetKeywordKind(parameterName) == SyntaxKind.None
            ? parameterName
            : "@" + parameterName;
        return SyntaxFactory.NameColon(
            SyntaxFactory.IdentifierName(SyntaxFactory.ParseToken(escaped)));
    }

    private static bool IsSameMethod(IMethodSymbol left, IMethodSymbol right)
    {
        left = left.ReducedFrom ?? left;
        right = right.ReducedFrom ?? right;
        return SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);
    }

    private static bool CanApplySignatureEditWithoutCollisions(
        ImmutableArray<IMethodSymbol> relatedMethods,
        SignatureEditPlan plan)
    {
        foreach (var method in relatedMethods)
        {
            if (method.ExplicitInterfaceImplementations.Length != 0)
                continue;

            var proposedParameters = GetProposedParameters(method, plan);
            foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (candidate.MethodKind != MethodKind.Ordinary ||
                    candidate.ExplicitInterfaceImplementations.Length != 0 ||
                    candidate.Arity != method.Arity ||
                    candidate.Parameters.Length != proposedParameters.Length ||
                    relatedMethods.Any(related => SymbolEqualityComparer.Default.Equals(related, candidate)))
                {
                    continue;
                }

                var collides = true;
                for (var index = 0; index < proposedParameters.Length; index++)
                {
                    var proposed = proposedParameters[index];
                    var existing = candidate.Parameters[index];
                    if (proposed is null)
                    {
                        if (existing.RefKind != RefKind.None ||
                            !IsControlParameter(existing, ControlParameterKind.CancellationToken))
                        {
                            collides = false;
                            break;
                        }
                    }
                    else if ((proposed.RefKind == RefKind.None) != (existing.RefKind == RefKind.None) ||
                             !AreSignatureTypesEquivalent(
                                 proposed.Type,
                                 method,
                                 existing.Type,
                                 candidate))
                    {
                        collides = false;
                        break;
                    }
                }

                if (collides)
                    return false;
            }
        }
        return true;
    }

    private static bool CanRemoveControlParametersWithoutBreakingNameReferences(
        ImmutableArray<IMethodSymbol> relatedMethods,
        ControlParameterKind kind,
        int keptOrdinal)
    {
        foreach (var method in relatedMethods)
        {
            var removedNames = method.Parameters
                .Where((parameter, ordinal) =>
                    IsControlParameter(parameter, kind) && ordinal != keptOrdinal)
                .Select(static parameter => parameter.Name)
                .ToImmutableHashSet(StringComparer.Ordinal);
            if (removedNames.Count == 0)
                continue;

            foreach (var attribute in method.Parameters.SelectMany(static parameter => parameter.GetAttributes()))
            {
                var metadataName = attribute.AttributeClass?.ToDisplayString();
                if (metadataName is not (
                        "System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute" or
                        "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute"))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.SelectMany(GetReferencedParameterNames)
                    .Any(removedNames.Contains))
                {
                    return false;
                }
            }

            foreach (var declaration in method.DeclaringSyntaxReferences
                         .Select(static reference => reference.GetSyntax())
                         .OfType<MethodDeclarationSyntax>())
            {
                if ((declaration.Body?.DescendantNodes().OfType<IdentifierNameSyntax>() ?? [])
                        .Concat(declaration.ExpressionBody?.Expression.DescendantNodesAndSelf()
                            .OfType<IdentifierNameSyntax>() ?? [])
                        .Any(identifier => removedNames.Contains(identifier.Identifier.ValueText)))
                {
                    return false;
                }

                var documentation = declaration.GetLeadingTrivia()
                    .Select(static trivia => trivia.GetStructure())
                    .OfType<DocumentationCommentTriviaSyntax>();
                foreach (var nameAttribute in documentation
                             .SelectMany(static comment => comment.DescendantNodes().OfType<XmlNameAttributeSyntax>()))
                {
                    var elementName = nameAttribute.Parent switch
                    {
                        XmlElementStartTagSyntax startTag => startTag.Name.LocalName.ValueText,
                        XmlEmptyElementSyntax emptyElement => emptyElement.Name.LocalName.ValueText,
                        _ => string.Empty
                    };
                    if (elementName is ("param" or "paramref") &&
                        removedNames.Contains(nameAttribute.Identifier.Identifier.ValueText))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static bool CanReorderControlParametersWithoutBreakingHandlerDependencies(
        ImmutableArray<IMethodSymbol> relatedMethods)
    {
        foreach (var method in relatedMethods)
        {
            var flags = GetControlParameterFlags(method);
            var order = GetControlParameterOrder(flags.CancellationTokens, flags.CallOptions);
            var proposedOrdinals = new int[order.Length];
            for (var ordinal = 0; ordinal < order.Length; ordinal++)
                proposedOrdinals[order[ordinal]] = ordinal;

            foreach (var handler in method.Parameters)
            {
                foreach (var attribute in handler.GetAttributes().Where(static attribute => string.Equals(
                             attribute.AttributeClass?.ToDisplayString(),
                             "System.Runtime.CompilerServices.InterpolatedStringHandlerArgumentAttribute",
                             StringComparison.Ordinal)))
                {
                    foreach (var name in attribute.ConstructorArguments.SelectMany(GetReferencedParameterNames))
                    {
                        var dependency = method.Parameters.FirstOrDefault(parameter =>
                            string.Equals(parameter.Name, name, StringComparison.Ordinal));
                        if (dependency is not null &&
                            proposedOrdinals[dependency.Ordinal] >= proposedOrdinals[handler.Ordinal])
                        {
                            return false;
                        }
                    }
                }
            }
        }
        return true;
    }

    private static IEnumerable<string> GetReferencedParameterNames(TypedConstant argument)
    {
        if (argument.Kind == TypedConstantKind.Array)
        {
            foreach (var item in argument.Values)
            {
                foreach (var name in GetReferencedParameterNames(item))
                    yield return name;
            }
        }
        else if (argument.Value is string name)
        {
            yield return name;
        }
    }

    private static ImmutableArray<IParameterSymbol?> GetProposedParameters(
        IMethodSymbol method,
        SignatureEditPlan plan)
    {
        IEnumerable<IParameterSymbol?> parameters = method.Parameters;
        switch (plan.Kind)
        {
            case SignatureEditKind.AddCancellationToken:
                {
                    var flags = GetControlParameterFlags(method);
                    parameters = GetControlParameterOrder(flags.CancellationTokens, flags.CallOptions)
                        .Select(ordinal => (IParameterSymbol?)method.Parameters[ordinal])
                        .Append(null);
                    break;
                }
            case SignatureEditKind.KeepControlParameter:
                parameters = method.Parameters.Where((parameter, ordinal) =>
                    !IsControlParameter(parameter, plan.ControlKind) || ordinal == plan.KeptOrdinal);
                break;
            case SignatureEditKind.ReorderControlParameters:
                {
                    var flags = GetControlParameterFlags(method);
                    parameters = GetControlParameterOrder(flags.CancellationTokens, flags.CallOptions)
                        .Select(ordinal => (IParameterSymbol?)method.Parameters[ordinal]);
                    break;
                }
        }
        return parameters.ToImmutableArray();
    }

    private static bool AreSignatureTypesEquivalent(
        ITypeSymbol left,
        IMethodSymbol leftMethod,
        ITypeSymbol right,
        IMethodSymbol rightMethod)
    {
        if (left is ITypeParameterSymbol leftParameter &&
            SymbolEqualityComparer.Default.Equals(leftParameter.ContainingSymbol, leftMethod))
        {
            return right is ITypeParameterSymbol rightParameter &&
                   SymbolEqualityComparer.Default.Equals(rightParameter.ContainingSymbol, rightMethod) &&
                   leftParameter.Ordinal == rightParameter.Ordinal;
        }
        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank &&
                   AreSignatureTypesEquivalent(
                       leftArray.ElementType,
                       leftMethod,
                       rightArray.ElementType,
                       rightMethod);
        }
        if (left is IPointerTypeSymbol leftPointer && right is IPointerTypeSymbol rightPointer)
        {
            return AreSignatureTypesEquivalent(
                leftPointer.PointedAtType,
                leftMethod,
                rightPointer.PointedAtType,
                rightMethod);
        }
        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            return SymbolEqualityComparer.Default.Equals(
                       leftNamed.OriginalDefinition,
                       rightNamed.OriginalDefinition) &&
                   leftNamed.TypeArguments.Length == rightNamed.TypeArguments.Length &&
                   leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, (leftArgument, rightArgument) =>
                           AreSignatureTypesEquivalent(
                               leftArgument,
                               leftMethod,
                               rightArgument,
                               rightMethod))
                       .All(static equivalent => equivalent);
        }
        return SymbolEqualityComparer.Default.Equals(left, right);
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
