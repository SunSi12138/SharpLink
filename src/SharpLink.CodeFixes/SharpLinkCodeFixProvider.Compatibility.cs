namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<Solution> RestoreMemberIdAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        string memberId,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        if (document is null)
            return solution;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node is null || semanticModel is null ||
            !TryGetRpcMemberTarget(node, semanticModel, cancellationToken, out var target, out var declaration) ||
            target is null)
        {
            return solution;
        }

        var attributeReferences = GetAttributesAcrossPartialPropertyParts(
                target, "SharpLink.Sdk.RpcMemberAttribute")
            .Select(static attribute => attribute.ApplicationSyntaxReference)
            .Where(static reference => reference is not null)
            .Select(static reference => reference!)
            .ToImmutableArray();
        if (!attributeReferences.IsDefaultOrEmpty)
        {
            foreach (var reference in attributeReferences)
            {
                var attributeDocument = solution.GetDocument(reference.SyntaxTree);
                var attributeRoot = attributeDocument is null
                    ? null
                    : await attributeDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var attribute = attributeRoot?.FindNode(reference.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
                if (attributeDocument is null || attributeRoot is null || attribute is null)
                    return solution;

                var restoredArgument = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(memberId));
                var updated = attribute.WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(restoredArgument)))
                    .WithAdditionalAnnotations(Formatter.Annotation);
                solution = solution.WithDocumentSyntaxRoot(
                    attributeDocument.Id, attributeRoot.ReplaceNode(attribute, updated));
            }
            return solution;
        }

        if (declaration is ParameterSyntax parameter)
        {
            var positionalArgument = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(memberId));
            var positionalAttributeList = CreateRpcMemberAttributeList(positionalArgument).WithTarget(
                SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.PropertyKeyword)));
            var changed = await ReplaceNodeAsync(
                document,
                parameter,
                parameter.AddAttributeLists(positionalAttributeList).WithAdditionalAnnotations(Formatter.Annotation),
                cancellationToken).ConfigureAwait(false);
            return changed.Project.Solution;
        }

        if (declaration is not MemberDeclarationSyntax member)
            return solution;

        var argument = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(memberId));
        var attributeList = CreateRpcMemberAttributeList(argument);
        var updatedMember = member switch
        {
            PropertyDeclarationSyntax property => property.AddAttributeLists(attributeList),
            FieldDeclarationSyntax field => field.AddAttributeLists(attributeList),
            _ => member
        };
        var updatedDocument = await ReplaceNodeAsync(
            document, member, updatedMember.WithAdditionalAnnotations(Formatter.Annotation), cancellationToken)
            .ConfigureAwait(false);
        return updatedDocument.Project.Solution;
    }

    private static async Task<bool> CanRestoreMemberIdAsync(
        Document document,
        Diagnostic diagnostic,
        uint memberId,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node is null || semanticModel is null ||
            !TryGetRpcMemberTarget(node, semanticModel, cancellationToken, out var target, out _))
        {
            return false;
        }
        if (target?.ContainingType is not { } containingType)
            return false;

        var rpcMemberAttributes = GetAttributesAcrossPartialPropertyParts(
            target, "SharpLink.Sdk.RpcMemberAttribute");
        if (rpcMemberAttributes.Length > 1 || rpcMemberAttributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(document.Project.Solution, reference.SyntaxTree)))
        {
            return false;
        }

        return !containingType.GetMembers()
            .Where(IsSerializableRpcMember)
            .Where(candidate => !SymbolEqualityComparer.Default.Equals(candidate, target))
            .Any(candidate => TryGetRpcMemberId(candidate, out var candidateId) && candidateId == memberId);
    }

    private static ImmutableArray<AttributeData> GetAttributesAcrossPartialPropertyParts(
        ISymbol target,
        string metadataName)
    {
        var owners = new List<ISymbol> { target };
        if (target is IPropertySymbol property)
        {
            if (property.PartialDefinitionPart is { } definition &&
                !owners.Any(owner => SymbolEqualityComparer.Default.Equals(owner, definition)))
            {
                owners.Add(definition);
            }
            if (property.PartialImplementationPart is { } implementation &&
                !owners.Any(owner => SymbolEqualityComparer.Default.Equals(owner, implementation)))
            {
                owners.Add(implementation);
            }
        }

        var result = new List<AttributeData>();
        foreach (var attribute in owners.SelectMany(static owner => owner.GetAttributes())
                     .Where(attribute => string.Equals(
                         attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal)))
        {
            var reference = attribute.ApplicationSyntaxReference;
            if (result.Any(existing =>
                    existing.ApplicationSyntaxReference?.SyntaxTree == reference?.SyntaxTree &&
                    existing.ApplicationSyntaxReference?.Span == reference?.Span))
            {
                continue;
            }
            result.Add(attribute);
        }
        return result.ToImmutableArray();
    }

    private static AttributeListSyntax CreateRpcMemberAttributeList(AttributeArgumentSyntax argument)
        => SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::SharpLink.Sdk.RpcMember"))
                    .WithArgumentList(SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(argument)))));

    private static bool TryGetRpcMemberTarget(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ISymbol? target,
        out SyntaxNode? declaration)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(static item => item is PropertyDeclarationSyntax or FieldDeclarationSyntax);
        switch (member)
        {
            case PropertyDeclarationSyntax property:
                target = semanticModel.GetDeclaredSymbol(property, cancellationToken);
                declaration = property;
                return target is not null;
            case FieldDeclarationSyntax { Declaration.Variables.Count: 1 } field:
                target = semanticModel.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken);
                declaration = field;
                return target is not null;
        }

        var parameter = node.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
        var record = parameter?.Parent?.Parent as RecordDeclarationSyntax;
        var recordType = record is null
            ? null
            : semanticModel.GetDeclaredSymbol(record, cancellationToken);
        target = parameter is null || recordType is null
            ? null
            : recordType.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault();
        declaration = target is null ? null : parameter;
        return target is not null;
    }

    private static async Task<bool> CanRestoreEnumUnderlyingTypeAsync(
        Document document,
        Diagnostic diagnostic,
        string underlyingType,
        CancellationToken cancellationToken)
    {
        if (!TryGetEnumUnderlyingTypeRange(underlyingType, out var minimum, out var maximum))
            return false;
        var declaration = await FindNodeAsync<EnumDeclarationSyntax>(
            document, diagnostic, cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var symbol = declaration is null
            ? null
            : semanticModel?.GetDeclaredSymbol(declaration, cancellationToken);
        if (symbol is null || semanticModel is null ||
            !TryGetEnumUnderlyingSpecialType(underlyingType, out var specialType))
            return false;

        var targetType = semanticModel.Compilation.GetSpecialType(specialType);
        foreach (var member in declaration!.Members)
        {
            var initializer = member.EqualsValue?.Value;
            if (initializer is null)
                continue;
            var initializerType = semanticModel.GetTypeInfo(initializer, cancellationToken).Type;
            if (!SymbolEqualityComparer.Default.Equals(initializerType, symbol) &&
                !semanticModel.ClassifyConversion(initializer, targetType).IsImplicit)
            {
                return false;
            }
        }

        foreach (var member in symbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(static field => field.HasConstantValue))
        {
            if (member.ConstantValue is null)
                return false;
            decimal value;
            try
            {
                value = Convert.ToDecimal(member.ConstantValue, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
            {
                return false;
            }
            if (value < minimum || value > maximum)
                return false;
        }
        return true;
    }

    private static async Task<Document> RestoreEnumUnderlyingTypeAsync(
        Document document,
        Diagnostic diagnostic,
        string underlyingType,
        CancellationToken cancellationToken)
    {
        var declaration = await FindNodeAsync<EnumDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || !TryGetEnumUnderlyingTypeSyntax(underlyingType, out var typeSyntax))
            return document;

        var baseList = SyntaxFactory.BaseList(
            SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(SyntaxFactory.SimpleBaseType(typeSyntax)));
        return await ReplaceNodeAsync(document, declaration,
            declaration.WithBaseList(baseList).WithAdditionalAnnotations(Formatter.Annotation), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Document> RestoreUnionTagAsync(
        Document document,
        Diagnostic diagnostic,
        string tag,
        string type,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null)
            return document;

        var typeName = type.StartsWith("global::", StringComparison.Ordinal) ? type : "global::" + type;
        var arguments = SyntaxFactory.SeparatedList(new[]
        {
            SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(tag)),
            SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeName)))
        });
        var updated = attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(arguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return await ReplaceNodeAsync(document, attribute, updated, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RegisterTimeoutFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var timeoutAttributes = equivalentMethods
            .SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsTimeoutAttribute)
            .Where(static attribute =>
                TryGetTimeoutSeconds(attribute, out var seconds) && !IsValidTimeoutSeconds(seconds))
            .ToImmutableArray();
        if (timeoutAttributes.Length == 0 ||
            timeoutAttributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
        {
            return;
        }

        var references = timeoutAttributes
            .Select(static attribute => attribute.ApplicationSyntaxReference!)
            .ToImmutableArray();
        RegisterSolutionFix(
            context,
            diagnostic,
            "Use generated default timeout",
            "UseDefaultTimeout",
            (solution, _, _, ct) => UpdateTimeoutAttributesAsync(solution, references, remove: false, ct));
        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [Timeout]",
            "RemoveTimeout",
            (solution, _, _, ct) => UpdateTimeoutAttributesAsync(solution, references, remove: true, ct));
    }

    private static async Task RegisterRemoveOnewayFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method)
        {
            return;
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var attributes = equivalentMethods
            .Where(static candidate => !IsValidOnewayReturnType(candidate.ReturnType))
            .SelectMany(static candidate => candidate.GetAttributes().Where(IsOnewayAttribute))
            .ToImmutableArray();
        if (attributes.Length == 0 || attributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
            return;

        var references = attributes.Select(static attribute => attribute.ApplicationSyntaxReference!)
            .ToImmutableArray();
        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [Oneway]",
            "RemoveOneway",
            (solution, _, _, ct) => RemoveAttributesAsync(solution, references, ct));
    }

    private static async Task<Solution> UpdateTimeoutAttributesAsync(
        Solution solution,
        ImmutableArray<SyntaxReference> references,
        bool remove,
        CancellationToken cancellationToken)
    {
        if (remove)
            return await RemoveAttributesAsync(solution, references, cancellationToken).ConfigureAwait(false);

        var referencesByDocument = references
            .Select(reference => (Reference: reference, Document: solution.GetDocument(reference.SyntaxTree)))
            .Where(static item => item.Document is not null)
            .GroupBy(static item => item.Document!.Id);
        foreach (var group in referencesByDocument)
        {
            var document = solution.GetDocument(group.Key);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;

            var attributes = group
                .Select(item => root.FindNode(item.Reference.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault())
                .Where(static attribute => attribute is not null)
                .Select(static attribute => attribute!)
                .Distinct()
                .ToArray();
            root = root.ReplaceNodes(attributes, static (_, current) =>
                current.WithArgumentList(null).WithAdditionalAnnotations(Formatter.Annotation));
            solution = solution.WithDocumentSyntaxRoot(group.Key, root);
        }
        return solution;
    }

    private static async Task<Solution> RemoveAttributesAsync(
        Solution solution,
        ImmutableArray<SyntaxReference> references,
        CancellationToken cancellationToken)
    {
        var spansByDocument = references
            .Select(reference => (Reference: reference, Document: solution.GetDocument(reference.SyntaxTree)))
            .Where(static item => item.Document is not null)
            .GroupBy(static item => item.Document!.Id, static item => item.Reference.Span);
        foreach (var group in spansByDocument)
        {
            var document = solution.GetDocument(group.Key);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;

            foreach (var span in group.Distinct().OrderByDescending(static span => span.Start))
            {
                var attribute = root.FindNode(span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
                if (attribute?.Parent is not AttributeListSyntax list)
                    continue;
                root = list.Attributes.Count == 1
                    ? root.RemoveNode(list, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root
                    : root.RemoveNode(attribute, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root;
            }
            solution = solution.WithDocumentSyntaxRoot(group.Key, root);
        }
        return solution;
    }

    private static async Task<Document> RemoveContainingAttributeAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        return attribute is null
            ? document
            : await RemoveAttributeNodeAsync(document, attribute, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> RemoveAttributeAtReferenceAsync(
        Solution solution,
        SyntaxReference reference,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(reference.SyntaxTree);
        var root = document is null
            ? null
            : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var attribute = root?.FindNode(reference.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (document is null || attribute is null)
            return solution;
        var updated = await RemoveAttributeNodeAsync(document, attribute, cancellationToken).ConfigureAwait(false);
        return updated.Project.Solution;
    }

    private static async Task<Document> RemoveAttributeNodeAsync(
        Document document,
        AttributeSyntax attribute,
        CancellationToken cancellationToken)
    {
        if (attribute.Parent is not AttributeListSyntax list)
            return document;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;
        SyntaxNode updatedRoot;
        if (list.Attributes.Count == 1)
        {
            updatedRoot = root.RemoveNode(list, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root;
        }
        else
        {
            updatedRoot = root.RemoveNode(attribute, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root;
        }
        return document.WithSyntaxRoot(updatedRoot);
    }
}
