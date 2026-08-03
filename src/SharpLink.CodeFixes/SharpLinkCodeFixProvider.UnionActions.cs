namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task RegisterUnionTagFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.PreviousUnionTag, out var tag) ||
            !diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.PreviousUnionType, out var type) ||
            !int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTag) ||
            parsedTag <= 0 || string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (semanticModel is null)
            return;
        var expected = type!.StartsWith("global::", StringComparison.Ordinal) ? type : "global::" + type;
        var typeSyntax = SyntaxFactory.ParseTypeName(expected);
        var resolvedType = typeSyntax.ContainsDiagnostics ||
                           typeSyntax.DescendantNodesAndSelf().OfType<OmittedTypeArgumentSyntax>().Any()
            ? null
            : semanticModel.GetSpeculativeTypeInfo(
                diagnostic.Location.SourceSpan.Start,
                typeSyntax,
                SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
        var unionDeclaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var unionType = unionDeclaration is null
            ? null
            : semanticModel.GetDeclaredSymbol(unionDeclaration, context.CancellationToken);
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var targetAttribute = root?.FindNode(
                diagnostic.Location.SourceSpan,
                getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (resolvedType is not INamedTypeSymbol namedCase || unionType is null ||
            targetAttribute is null ||
            !TryGetUnionCaseArguments(
                targetAttribute, semanticModel, context.CancellationToken, out _, out _) ||
            namedCase.IsUnboundGenericType || ContainsTypeParameter(namedCase) ||
            namedCase.TypeKind is not (TypeKind.Class or TypeKind.Struct) || namedCase.IsAbstract ||
            IsObsoleteWithError(namedCase) ||
            !semanticModel.IsAccessible(diagnostic.Location.SourceSpan.Start, namedCase) ||
            (namedCase.DeclaringSyntaxReferences.Length != 0 &&
             !HasOnlyRegularEditableDeclarations(namedCase, context.Document.Project.Solution)) ||
            semanticModel.Compilation is not CSharpCompilation csharpCompilation ||
            !csharpCompilation.ClassifyConversion(namedCase, unionType).IsImplicit)
            return;

        var mappings = unionType.GetAttributes().Where(IsRpcUnionCaseAttribute).ToArray();
        var targetMapping = mappings.FirstOrDefault(attribute => IsAttributeApplication(attribute, targetAttribute));
        if (targetMapping is null || targetMapping.ConstructorArguments.Length != 2 ||
            targetMapping.ConstructorArguments[0].Value is not int currentTag ||
            targetMapping.ConstructorArguments[1].Value is not ITypeSymbol currentCase)
        {
            return;
        }

        var preserveCurrentCase = !SymbolEqualityComparer.Default.Equals(currentCase, namedCase);
        var otherMappings = mappings.Where(attribute => !IsAttributeApplication(attribute, targetAttribute)).ToArray();
        if (otherMappings.Any(attribute =>
                attribute.ConstructorArguments.Length == 2 &&
                (attribute.ConstructorArguments[0].Value is int existingTag && existingTag == parsedTag ||
                 attribute.ConstructorArguments[1].Value is ITypeSymbol existingCase &&
                 (SymbolEqualityComparer.Default.Equals(existingCase, namedCase) ||
                  preserveCurrentCase && SymbolEqualityComparer.Default.Equals(existingCase, currentCase)))))
        {
            return;
        }

        int? preservedTag = null;
        if (preserveCurrentCase)
        {
            if (!TryGetPublishedUnionTags(diagnostic, parsedTag, out var usedTags))
                return;
            usedTags.UnionWith(otherMappings
                .Where(static attribute => attribute.ConstructorArguments.Length == 2)
                .Select(static attribute => attribute.ConstructorArguments[0].Value)
                .OfType<int>()
                .Where(static item => item > 0));
            if (currentTag > 0 && !usedTags.Contains(currentTag))
            {
                preservedTag = currentTag;
            }
            else
            {
                for (var candidate = 1; candidate <= usedTags.Count + 1; candidate++)
                {
                    if (!usedTags.Contains(candidate))
                    {
                        preservedTag = candidate;
                        break;
                    }
                }
            }
            if (preservedTag is null)
                return;
        }

        RegisterDocumentFix(context, diagnostic, $"Restore tag {tag} to {type}",
            "RestoreUnionTag",
            (document, item, ct) => RestoreUnionTagAsync(
                document, item, tag!, type!, preservedTag, ct));
    }

    private static bool TryGetPublishedUnionTags(
        Diagnostic diagnostic,
        int previousTag,
        out HashSet<int> publishedTags)
    {
        publishedTags = [];
        if (!diagnostic.Properties.TryGetValue(
                SharpLinkDiagnosticProperties.PublishedUnionTags, out var serializedTags) ||
            string.IsNullOrWhiteSpace(serializedTags))
        {
            return false;
        }

        foreach (var serializedTag in serializedTags!.Split(','))
        {
            if (!int.TryParse(
                    serializedTag,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var publishedTag) ||
                publishedTag <= 0)
            {
                publishedTags.Clear();
                return false;
            }
            publishedTags.Add(publishedTag);
        }
        return publishedTags.Contains(previousTag);
    }

    private static bool IsAttributeApplication(AttributeData attribute, AttributeSyntax syntax)
        => attribute.ApplicationSyntaxReference is { } reference &&
           reference.SyntaxTree == syntax.SyntaxTree &&
           reference.Span == syntax.Span;
}
