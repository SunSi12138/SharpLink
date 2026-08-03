namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static SeparatedSyntaxList<TNode> RebuildSeparatedListPreservingTrivia<TNode>(
        SeparatedSyntaxList<TNode> original,
        IEnumerable<(int ordinal, TNode node)> retained,
        TNode? appended = null)
        where TNode : SyntaxNode
    {
        var items = retained.ToArray();
        var positions = items
            .Select(static (item, position) => (item.ordinal, position))
            .ToDictionary(static item => item.ordinal, static item => item.position);
        var nodes = items.Select(static item => item.node).ToList();
        if (appended is not null)
            nodes.Add(appended);

        var separators = new SyntaxToken[Math.Max(0, nodes.Count - 1)];
        var separatorSources = Enumerable.Repeat(-1, separators.Length).ToArray();
        var usedSeparators = new bool[original.SeparatorCount];
        for (var boundary = 0; boundary < separators.Length; boundary++)
        {
            if (boundary < items.Length && items[boundary].ordinal < original.SeparatorCount)
            {
                var separatorIndex = items[boundary].ordinal;
                separators[boundary] = original.GetSeparator(separatorIndex);
                separatorSources[boundary] = separatorIndex;
                usedSeparators[separatorIndex] = true;
            }
            else
            {
                separators[boundary] = SyntaxFactory.Token(SyntaxKind.CommaToken);
            }
        }

        for (var separatorIndex = 0; separatorIndex < original.SeparatorCount; separatorIndex++)
        {
            if (usedSeparators[separatorIndex])
                continue;
            var orphaned = original.GetSeparator(separatorIndex);
            if (separators.Length == 0)
            {
                if (nodes.Count != 0)
                {
                    nodes[0] = (TNode)nodes[0].WithTrailingTrivia(
                        nodes[0].GetTrailingTrivia()
                            .AddRange(orphaned.LeadingTrivia)
                            .AddRange(orphaned.TrailingTrivia));
                }
                continue;
            }

            int boundary;
            if (positions.TryGetValue(separatorIndex, out var leftPosition))
            {
                boundary = Math.Min(leftPosition, separators.Length - 1);
            }
            else
            {
                var nextOrdinal = positions.Keys
                    .Where(ordinal => ordinal > separatorIndex)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                boundary = nextOrdinal == int.MaxValue
                    ? separators.Length - 1
                    : Math.Max(0, positions[nextOrdinal] - 1);
            }

            if (separatorSources[boundary] < 0)
            {
                separators[boundary] = orphaned;
                separatorSources[boundary] = separatorIndex;
            }
            else
            {
                separators[boundary] = separators[boundary]
                    .WithLeadingTrivia(separators[boundary].LeadingTrivia.AddRange(orphaned.LeadingTrivia))
                    .WithTrailingTrivia(MergeSeparatorTrailingTrivia(
                        separators[boundary].TrailingTrivia,
                        orphaned.TrailingTrivia));
            }
        }

        var nodesAndTokens = new List<SyntaxNodeOrToken>(nodes.Count + separators.Length);
        for (var index = 0; index < nodes.Count; index++)
        {
            nodesAndTokens.Add(nodes[index]);
            if (index < separators.Length)
                nodesAndTokens.Add(separators[index]);
        }
        return SyntaxFactory.SeparatedList<TNode>(nodesAndTokens);
    }

    private static SyntaxTriviaList MergeSeparatorTrailingTrivia(
        SyntaxTriviaList existing,
        SyntaxTriviaList additional)
    {
        var merged = existing.AddRange(additional).ToArray();
        var commentIndex = Array.FindIndex(merged, static trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
        if (commentIndex < 0 || !merged.Take(commentIndex).Any(static trivia =>
                trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return SyntaxFactory.TriviaList(merged);
        }

        var reordered = new List<SyntaxTrivia>(merged.Length);
        reordered.AddRange(merged.Take(commentIndex).Where(static trivia =>
            !trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        reordered.Add(merged[commentIndex]);
        reordered.AddRange(merged.Take(commentIndex).Where(static trivia =>
            trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        reordered.AddRange(merged.Skip(commentIndex + 1));
        return SyntaxFactory.TriviaList(reordered);
    }

    private static ArgumentSyntax NameArgument(
        ArgumentSyntax argument,
        int ordinal,
        InvocationEdit edit)
    {
        if (ordinal < 0 || ordinal >= edit.ParameterNames.Length)
            return argument;
        var replacement = CreateNameColon(edit.ParameterNames[ordinal]);
        if (argument.NameColon is { } existing)
        {
            replacement = existing.WithName(
                replacement.Name.WithTriviaFrom(existing.Name));
        }
        return argument.WithNameColon(replacement);
    }

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

            var attributes = method.Parameters.SelectMany(static parameter => parameter.GetAttributes())
                .Concat(method.GetAttributes())
                .Concat(method.GetReturnTypeAttributes());
            foreach (var attribute in attributes)
            {
                if (attribute.ConstructorArguments.SelectMany(GetReferencedParameterNames)
                        .Concat(attribute.NamedArguments.SelectMany(static argument =>
                            GetReferencedParameterNames(argument.Value)))
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
                   (leftNamed.ContainingType is null && rightNamed.ContainingType is null ||
                    leftNamed.ContainingType is not null && rightNamed.ContainingType is not null &&
                    AreSignatureTypesEquivalent(
                        leftNamed.ContainingType,
                        leftMethod,
                        rightNamed.ContainingType,
                        rightMethod)) &&
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
        string baseName,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(
            methods.SelectMany(static item => item.Parameters).Select(static item => item.Name),
            StringComparer.Ordinal);
        foreach (var reference in methods.SelectMany(static method => method.DeclaringSyntaxReferences))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var token in reference.GetSyntax(cancellationToken).DescendantTokens()
                         .Where(static token => token.IsKind(SyntaxKind.IdentifierToken)))
            {
                names.Add(token.ValueText);
            }
        }
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
