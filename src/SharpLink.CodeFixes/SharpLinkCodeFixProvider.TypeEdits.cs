namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<Document> MakeContainingTypesPublicAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;
        var declaration = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (declaration is null)
            return document;
        var declarations = declaration.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().ToArray();
        var updatedRoot = root.ReplaceNodes(declarations, static (_, current) => MakePublic(current));
        return document.WithSyntaxRoot(updatedRoot);
    }

    private static async Task<Solution> MakeContainingTypesPublicAcrossSolutionAsync(
        Solution solution,
        DocumentId documentId,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        var declaration = document is null
            ? null
            : await FindNodeAsync<BaseTypeDeclarationSyntax>(document, diagnostic, cancellationToken)
                .ConfigureAwait(false);
        var semanticModel = document is null
            ? null
            : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, cancellationToken)
            is not INamedTypeSymbol symbol)
        {
            return solution;
        }

        if (!TryGetPublicizationClosure(symbol, solution, out var publicizedTypes))
            return solution;

        var referencesByDocument = new Dictionary<DocumentId, List<Microsoft.CodeAnalysis.Text.TextSpan>>();
        foreach (var current in publicizedTypes)
        {
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var target = solution.GetDocument(reference.SyntaxTree);
                if (target is null)
                    continue;
                if (!referencesByDocument.TryGetValue(target.Id, out var spans))
                {
                    spans = [];
                    referencesByDocument.Add(target.Id, spans);
                }
                spans.Add(reference.Span);
            }
        }

        foreach (var pair in referencesByDocument)
        {
            var target = solution.GetDocument(pair.Key);
            var root = target is null ? null : await target.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;
            var declarations = pair.Value
                .Select(span => FindPublicizableTypeDeclaration(root, span))
                .Where(static item => item is not null)
                .Select(static item => item!)
                .Distinct()
                .ToArray();
            var updatedRoot = root.ReplaceNodes(declarations, static (_, current) =>
                MakePublic(current).WithAdditionalAnnotations(Formatter.Annotation));
            solution = solution.WithDocumentSyntaxRoot(pair.Key, updatedRoot);
        }
        return solution;
    }

    private static async Task<Solution> FixServiceShapeAcrossSolutionAsync(
        Solution solution,
        INamedTypeSymbol type,
        bool makePublic,
        CancellationToken cancellationToken)
    {
        var referencesByDocument = new Dictionary<
            DocumentId,
            Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, bool>>();
        ImmutableArray<INamedTypeSymbol> typesToUpdate;
        if (makePublic)
        {
            if (!TryGetPublicizationClosure(type, solution, out typesToUpdate))
                return solution;
        }
        else
        {
            typesToUpdate = ImmutableArray.Create(type);
        }
        foreach (var current in typesToUpdate)
        {
            var isService = SymbolEqualityComparer.Default.Equals(current, type);
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                if (!referencesByDocument.TryGetValue(document.Id, out var spans))
                {
                    spans = [];
                    referencesByDocument.Add(document.Id, spans);
                }
                spans[reference.Span] = isService;
            }
        }

        foreach (var pair in referencesByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;
            var declarations = new Dictionary<MemberDeclarationSyntax, bool>();
            foreach (var item in pair.Value)
            {
                var declaration = FindPublicizableTypeDeclaration(root, item.Key);
                if (declaration is not null)
                    declarations[declaration] = item.Value;
            }
            var updatedRoot = root.ReplaceNodes(declarations.Keys, (original, current) =>
            {
                MemberDeclarationSyntax updated = current;
                if (declarations[original] && updated is TypeDeclarationSyntax service)
                    updated = RemoveModifier(service, SyntaxKind.AbstractKeyword);
                if (makePublic)
                    updated = MakePublic(updated);
                return updated.WithAdditionalAnnotations(Formatter.Annotation);
            });
            solution = solution.WithDocumentSyntaxRoot(pair.Key, updatedRoot);
        }
        return solution;
    }

    private static async Task<Document> UpdateTypeAtDiagnosticAsync(
        Document document,
        Diagnostic diagnostic,
        Func<TypeDeclarationSyntax, TypeDeclarationSyntax> transform,
        CancellationToken cancellationToken)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (declaration is null)
            return document;
        return await ReplaceNodeAsync(document, declaration,
            transform(declaration).WithAdditionalAnnotations(Formatter.Annotation), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Solution> AddAttributeToSymbolAsync(
        Solution solution,
        ISymbol symbol,
        string attributeName,
        CancellationToken cancellationToken)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault(candidate =>
            IsRegularEditableDocument(solution, candidate.SyntaxTree));
        if (reference is null)
            return solution;
        var document = solution.GetDocument(reference.SyntaxTree);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(reference.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (document is null || declaration is null)
            return solution;

        var attributeList = CreateAttributeList(attributeName);
        MemberDeclarationSyntax updated = declaration switch
        {
            RecordDeclarationSyntax record when symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } =>
                record.AddAttributeLists(attributeList.WithTarget(
                    SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.MethodKeyword)))),
            BaseTypeDeclarationSyntax type => type.AddAttributeLists(attributeList),
            MethodDeclarationSyntax method => method.AddAttributeLists(attributeList),
            ConstructorDeclarationSyntax constructor => constructor.AddAttributeLists(attributeList),
            _ => declaration
        };
        var updatedRoot = root!.ReplaceNode(
            declaration, updated.WithAdditionalAnnotations(Formatter.Annotation));
        return solution.WithDocumentSyntaxRoot(document.Id, updatedRoot);
    }

    private static async Task<Solution> SelectServiceConstructorAsync(
        Solution solution,
        ImmutableArray<SyntaxReference> markerReferences,
        IMethodSymbol selectedConstructor,
        CancellationToken cancellationToken)
    {
        var selectedReference = selectedConstructor.DeclaringSyntaxReferences.FirstOrDefault(reference =>
            IsRegularEditableDocument(solution, reference.SyntaxTree) &&
            reference.GetSyntax(cancellationToken).AncestorsAndSelf().Any(static syntax =>
                syntax is ConstructorDeclarationSyntax or RecordDeclarationSyntax));
        var selectedDocument = selectedReference is null
            ? null
            : solution.GetDocument(selectedReference.SyntaxTree);
        if (selectedReference is null || selectedDocument is null)
            return solution;

        var markerSpansByDocument = markerReferences
            .Select(reference => (Reference: reference, Document: solution.GetDocument(reference.SyntaxTree)))
            .Where(static item => item.Document is not null)
            .GroupBy(static item => item.Document!.Id, static item => item.Reference.Span)
            .ToDictionary(static group => group.Key, static group => group.Distinct().ToArray());
        var documentIds = markerSpansByDocument.Keys.Append(selectedDocument.Id).Distinct().ToArray();
        foreach (var documentId in documentIds)
        {
            var document = solution.GetDocument(documentId);
            var root = document is null
                ? null
                : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                continue;

            SyntaxAnnotation? selectedAnnotation = null;
            if (documentId == selectedDocument.Id)
            {
                var declaration = root.FindNode(selectedReference.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                if (declaration is null)
                    return solution;
                selectedAnnotation = new SyntaxAnnotation();
                root = root.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(selectedAnnotation));
            }

            if (markerSpansByDocument.TryGetValue(documentId, out var markerSpans))
            {
                foreach (var span in markerSpans.OrderByDescending(static span => span.Start))
                {
                    var attribute = root.FindNode(span, getInnermostNodeForTie: true)
                        .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
                    if (attribute?.Parent is not AttributeListSyntax list)
                        continue;
                    root = list.Attributes.Count == 1
                        ? root.RemoveNode(list, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root
                        : root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
                }
            }

            if (selectedAnnotation is not null)
            {
                var declaration = root.GetAnnotatedNodes(selectedAnnotation)
                    .OfType<MemberDeclarationSyntax>().SingleOrDefault();
                if (declaration is null)
                    return solution;
                var attributeList = CreateAttributeList(
                    "global::Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor");
                MemberDeclarationSyntax updated = declaration switch
                {
                    RecordDeclarationSyntax record => record.AddAttributeLists(attributeList.WithTarget(
                        SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.MethodKeyword)))),
                    ConstructorDeclarationSyntax constructor => constructor.AddAttributeLists(attributeList),
                    _ => declaration
                };
                root = root.ReplaceNode(
                    declaration, updated.WithAdditionalAnnotations(Formatter.Annotation));
            }

            solution = solution.WithDocumentSyntaxRoot(documentId, root);
        }
        return solution;
    }

    private static async Task<Solution> MakeConstructorPublicAsync(
        Solution solution,
        IMethodSymbol constructor,
        CancellationToken cancellationToken)
    {
        var reference = constructor.DeclaringSyntaxReferences.FirstOrDefault(candidate =>
            IsRegularEditableDocument(solution, candidate.SyntaxTree));
        var document = reference is null ? null : solution.GetDocument(reference.SyntaxTree);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(reference!.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (document is null || declaration is null)
            return solution;
        var updated = declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return solution.WithDocumentSyntaxRoot(document.Id, root!.ReplaceNode(declaration, updated));
    }

    private static async Task<Solution> FixAdapterShapeAsync(
        Solution solution,
        INamedTypeSymbol adapter,
        CancellationToken cancellationToken)
    {
        if (!TryGetPublicizationClosure(adapter, solution, out var publicizedTypes))
            return solution;

        var declarationsByDocument = new Dictionary<
            DocumentId,
            Dictionary<Microsoft.CodeAnalysis.Text.TextSpan, bool>>();
        foreach (var current in publicizedTypes)
        {
            var isAdapter = SymbolEqualityComparer.Default.Equals(current, adapter);
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                if (!declarationsByDocument.TryGetValue(document.Id, out var spans))
                {
                    spans = [];
                    declarationsByDocument.Add(document.Id, spans);
                }
                spans[reference.Span] = isAdapter;
            }
        }

        var firstAdapterReference = adapter.DeclaringSyntaxReferences[0];
        var firstAdapterDocument = solution.GetDocument(firstAdapterReference.SyntaxTree)?.Id;
        var needsPublicParameterlessConstructor = !adapter.InstanceConstructors.Any(static constructor =>
            constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0);
        var hasExplicitParameterlessConstructor = adapter.InstanceConstructors.Any(static constructor =>
            !constructor.IsImplicitlyDeclared && constructor.Parameters.Length == 0);

        foreach (var pair in declarationsByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;

            var declarations = new Dictionary<MemberDeclarationSyntax, bool>();
            foreach (var item in pair.Value)
            {
                var declaration = FindPublicizableTypeDeclaration(root, item.Key);
                if (declaration is not null)
                    declarations[declaration] = item.Value;
            }

            var updatedRoot = root.ReplaceNodes(declarations.Keys, (original, rewritten) =>
            {
                var accessible = MakePublic(rewritten);
                if (!declarations[original] || accessible is not ClassDeclarationSyntax adapterClass)
                    return accessible.WithAdditionalAnnotations(Formatter.Annotation);

                var modifiers = AddModifier(
                    RemoveModifier(adapterClass.Modifiers, SyntaxKind.AbstractKeyword),
                    SyntaxKind.SealedKeyword);
                adapterClass = adapterClass.WithModifiers(modifiers);

                if (needsPublicParameterlessConstructor)
                {
                    var constructor = adapterClass.Members.OfType<ConstructorDeclarationSyntax>()
                        .FirstOrDefault(static item =>
                            item.ParameterList.Parameters.Count == 0 &&
                            !item.Modifiers.Any(SyntaxKind.StaticKeyword));
                    if (constructor is not null)
                    {
                        adapterClass = adapterClass.ReplaceNode(constructor,
                            constructor.WithModifiers(WithAccessibility(
                                constructor.Modifiers, SyntaxKind.PublicKeyword)));
                    }
                    else if (!hasExplicitParameterlessConstructor &&
                             pair.Key == firstAdapterDocument &&
                             original.Span == firstAdapterReference.Span)
                    {
                        adapterClass = adapterClass.AddMembers(
                            SyntaxFactory.ConstructorDeclaration(adapter.Name)
                                .WithModifiers(SyntaxFactory.TokenList(
                                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                                .WithBody(SyntaxFactory.Block()));
                    }
                }

                return adapterClass.WithAdditionalAnnotations(Formatter.Annotation);
            });
            solution = solution.WithDocumentSyntaxRoot(pair.Key, updatedRoot);
        }
        return solution;
    }

    private static MemberDeclarationSyntax? FindPublicizableTypeDeclaration(
        SyntaxNode root,
        Microsoft.CodeAnalysis.Text.TextSpan span)
        => root.FindNode(span, getInnermostNodeForTie: true)
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(IsPublicizableTypeDeclaration);
}
