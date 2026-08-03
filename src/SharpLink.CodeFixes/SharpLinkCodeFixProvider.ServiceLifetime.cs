namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task RegisterServiceLifetimeFixesAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var service = declaration is null
            ? null
            : semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken);
        if (service?.TypeKind != TypeKind.Class || semanticModel is null || IsObsoleteWithError(service) ||
            !HasValidServiceActivationShape(service))
            return;

        var serviceAttribute = service.GetAttributes().FirstOrDefault(IsRpcServiceAttribute);
        var attributeReference = serviceAttribute?.ApplicationSyntaxReference;
        var attributeSyntax = attributeReference is null
            ? null
            : await attributeReference.GetSyntaxAsync(context.CancellationToken).ConfigureAwait(false) as AttributeSyntax;
        var lifetimeProperty = FindAttributeProperty(serviceAttribute?.AttributeClass, "Lifetime");
        var lifetimeType = lifetimeProperty?.Type as INamedTypeSymbol;
        var lifetimeTypeName = lifetimeType is null
            ? null
            : GetBindableTypeName(
                lifetimeType,
                semanticModel,
                diagnostic.Location.SourceSpan.Start,
                GetExternAlias(attributeSyntax));
        if (attributeReference is null || lifetimeType?.TypeKind != TypeKind.Enum ||
            lifetimeTypeName is null ||
            !IsRegularEditableDocument(context.Document.Project.Solution, attributeReference.SyntaxTree))
        {
            return;
        }

        var lifetimes = new[]
        {
            (Name: "Singleton", Value: 0),
            (Name: "Connection", Value: 1),
            (Name: "Call", Value: 2)
        };
        foreach (var lifetime in lifetimes)
        {
            var member = lifetimeType.GetMembers(lifetime.Name).OfType<IFieldSymbol>()
                .SingleOrDefault(field => field.HasConstantValue && field.ConstantValue is not null &&
                    Convert.ToDecimal(field.ConstantValue, CultureInfo.InvariantCulture) == lifetime.Value);
            if (member is null || member.DeclaredAccessibility != Accessibility.Public ||
                IsObsoleteWithError(member) ||
                !semanticModel.IsAccessible(diagnostic.Location.SourceSpan.Start, member))
            {
                continue;
            }

            var name = lifetime.Name;
            RegisterSolutionFix(context, diagnostic, $"Set RPC service lifetime to {name}",
                "SetLifetime:" + name,
                (solution, _, _, ct) => SetServiceLifetimeAsync(
                    solution, attributeReference, lifetimeTypeName!, name, ct));
        }
    }

    private static IPropertySymbol? FindAttributeProperty(INamedTypeSymbol? attributeType, string name)
    {
        for (var current = attributeType; current is not null; current = current.BaseType)
        {
            var property = current.GetMembers(name).OfType<IPropertySymbol>()
                .SingleOrDefault(static candidate => !candidate.IsStatic && candidate.Parameters.Length == 0);
            if (property is not null)
                return property;
        }
        return null;
    }

    private static string? GetBindableTypeName(
        INamedTypeSymbol type,
        SemanticModel semanticModel,
        int position,
        string? externAlias)
    {
        var fullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var candidates = new List<string> { fullyQualifiedName };
        if (externAlias is not null && fullyQualifiedName.StartsWith("global::", StringComparison.Ordinal))
        {
            candidates.Add(externAlias + "::" +
                           fullyQualifiedName.Substring("global::".Length));
        }
        candidates.Add(type.ToMinimalDisplayString(semanticModel, position));
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var syntax = SyntaxFactory.ParseTypeName(candidate);
            if (syntax.ContainsDiagnostics)
                continue;
            var boundType = semanticModel.GetSpeculativeTypeInfo(
                position,
                syntax,
                SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            if (SymbolEqualityComparer.Default.Equals(boundType, type))
                return candidate;
        }
        return null;
    }

    private static string? GetExternAlias(AttributeSyntax? attribute)
        => attribute?.Name.DescendantNodesAndSelf().OfType<AliasQualifiedNameSyntax>()
            .FirstOrDefault()?.Alias.Identifier.ValueText;

    private static async Task<Solution> SetServiceLifetimeAsync(
        Solution solution,
        SyntaxReference attributeReference,
        string lifetimeTypeName,
        string lifetime,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(attributeReference.SyntaxTree);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false);
        var attribute = root?.FindNode(attributeReference.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (document is null || root is null || attribute is null)
            return solution;

        var value = SyntaxFactory.ParseExpression(lifetimeTypeName + "." + lifetime);
        var arguments = attribute.ArgumentList?.Arguments ?? default;
        var existing = arguments.FirstOrDefault(static item => item.NameEquals?.Name.Identifier.ValueText == "Lifetime");
        var updatedArguments = existing is null
            ? arguments.Add(SyntaxFactory.AttributeArgument(value)
                .WithNameEquals(SyntaxFactory.NameEquals("Lifetime")))
            : arguments.Replace(
                existing,
                existing.WithExpression(value.WithTriviaFrom(existing.Expression)));
        var updatedArgumentList = attribute.ArgumentList is { } argumentList
            ? argumentList.WithArguments(updatedArguments)
            : SyntaxFactory.AttributeArgumentList(updatedArguments);
        var updated = attribute.WithArgumentList(updatedArgumentList)
            .WithAdditionalAnnotations(Formatter.Annotation);
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(attribute, updated));
    }
}
