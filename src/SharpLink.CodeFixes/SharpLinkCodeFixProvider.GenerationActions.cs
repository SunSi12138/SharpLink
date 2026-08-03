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

        if (unionType.GetAttributes().Where(IsRpcUnionCaseAttribute).Any(attribute =>
                !IsAttributeApplication(attribute, targetAttribute) &&
                attribute.ConstructorArguments.Length == 2 &&
                (attribute.ConstructorArguments[0].Value is int existingTag && existingTag == parsedTag ||
                 attribute.ConstructorArguments[1].Value is ITypeSymbol existingCase &&
                 SymbolEqualityComparer.Default.Equals(existingCase, namedCase))))
        {
            return;
        }

        RegisterDocumentFix(context, diagnostic, $"Restore tag {tag} to {type}",
            "RestoreUnionTag",
            (document, item, ct) => RestoreUnionTagAsync(document, item, tag!, type!, ct));
    }

    private static bool IsAttributeApplication(AttributeData attribute, AttributeSyntax syntax)
        => attribute.ApplicationSyntaxReference is { } reference &&
           reference.SyntaxTree == syntax.SyntaxTree &&
           reference.Span == syntax.Span;

    private static async Task RegisterRestoreServiceRouteFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<InterfaceDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } contract ||
            IsObsoleteWithError(contract) ||
            !HasValidRpcContractShapeForAnnotation(contract) ||
            !SharpLink.Generator.RpcGenerator.CanGenerateContractPayloadCodecs(
                semanticModel.Compilation, contract, context.CancellationToken))
            return;

        var implementations = await SymbolFinder.FindImplementationsAsync(
            contract, context.Document.Project.Solution, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        var candidates = implementations
            .OfType<INamedTypeSymbol>()
            .Where(static item => item.TypeKind == TypeKind.Class && !item.IsAbstract && !item.IsGenericType)
            .Where(static item => item.Locations.Any(static location => location.IsInSource))
            .Where(item => HasOnlyRegularEditableDeclarations(
                item, context.Document.Project.Solution))
            .Where(item => HasDeclarationInProject(
                item, context.Document.Project.Solution, context.Document.Project.Id))
            .Where(IsAccessibleFromGeneratedCode)
            .Where(static item => !HasRpcServiceAttribute(item))
            .Where(static item => item.AllInterfaces.Count(HasRpcContractAttribute) == 1)
            .Where(HasValidServiceActivationShape)
            .ToArray();
        if (candidates.Length != 1)
            return;

        var service = candidates[0];
        RegisterSolutionFix(context, diagnostic, $"Add [RpcService] to {service.Name}", "RestoreServiceRoute",
            (solution, _, _, ct) => AddAttributeToSymbolAsync(
                solution, service, "global::SharpLink.Sdk.RpcService", ct));
    }

    private static async Task RegisterAdapterShapeFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null || semanticModel is null)
            return;

        var adapterTypeSyntax = attribute.ArgumentList?.Arguments
            .Select(static argument => argument.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .Select(static expression => expression.Type)
            .FirstOrDefault(typeSyntax => semanticModel.GetTypeInfo(typeSyntax, context.CancellationToken).Type is INamedTypeSymbol);
        if (adapterTypeSyntax is null ||
            semanticModel.GetTypeInfo(adapterTypeSyntax, context.CancellationToken).Type is not INamedTypeSymbol adapter ||
            !adapter.AllInterfaces.Any(IsCodecAdapter) ||
            adapter.TypeKind != TypeKind.Class || adapter.IsGenericType ||
            GetContainingTypes(adapter).Any(static item => item.IsGenericType) ||
            (adapter.IsAbstract && !IsSafeToMakeConcrete(adapter)) ||
            HasMembersIncompatibleWithSealing(adapter, allowParameterlessConstructorPublicization: true) ||
            !CanExposePublicParameterlessConstructor(adapter) ||
            !CanCallParameterlessConstructorWithRequiredMembers(adapter) ||
            HasPrimaryConstructorWithoutParameterlessAlternative(adapter, context.CancellationToken) ||
            !TryGetPublicizationClosure(adapter, context.Document.Project.Solution, out _) ||
            adapter.DeclaringSyntaxReferences.Length == 0 ||
            adapter.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(context.CancellationToken) is not ClassDeclarationSyntax))
        {
            return;
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            adapter,
            context.Document.Project.Solution,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        if (derivedTypes.Any(static type => type.Locations.Any(static location => location.IsInSource)))
            return;

        RegisterSolutionFix(context, diagnostic, $"Fix {adapter.Name} Codec adapter shape", "FixAdapterShape",
            (solution, _, _, ct) => FixAdapterShapeAsync(solution, adapter, ct));
    }

    private static async Task RegisterMakeInstanceMethodFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var method = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (method is null || semanticModel?.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol ||
            !symbol.IsStatic || symbol.MethodKind != MethodKind.Ordinary || IsObsoleteWithError(symbol) ||
            symbol.DeclaringSyntaxReferences.Any(static reference =>
                reference.GetSyntax() is not MethodDeclarationSyntax) ||
            !await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: false,
                allowSignatureQualifiedCrefs: true, context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, "Make RPC method an instance method",
            SignatureKeyPrefix + "MakeInstance", MakeInstanceMethodAsync);
    }

    private static async Task RegisterAddIServiceFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<InterfaceDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol contract ||
            IsObsoleteWithError(contract) ||
            contract.Arity != 0 ||
            GetContainingTypes(contract).Any(static containing => containing.Arity != 0) ||
            !HasOnlyRegularEditableDeclarations(contract, context.Document.Project.Solution) ||
            !SharpLink.Generator.RpcGenerator.CanGenerateContractPayloadCodecs(
                semanticModel.Compilation, contract, context.CancellationToken) ||
            !HasValidRpcContractShapeForAnnotation(contract))
        {
            return;
        }

        if (await CountRpcServiceImplementationsAsync(
                contract,
                excludedService: null,
                context.Document.Project.Solution,
                context.CancellationToken).ConfigureAwait(false) > 1)
        {
            return;
        }

        RegisterDocumentFix(context, diagnostic, "Add IService to RPC contract", "AddIService",
            AddIServiceAsync);
    }

    private static async Task<int> CountRpcServiceImplementationsAsync(
        INamedTypeSymbol contract,
        INamedTypeSymbol? excludedService,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var implementations = await SymbolFinder.FindImplementationsAsync(
            contract,
            solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return implementations.OfType<INamedTypeSymbol>().Count(implementation =>
            implementation.TypeKind == TypeKind.Class &&
            (excludedService is null ||
             !SymbolEqualityComparer.Default.Equals(implementation, excludedService)) &&
            HasRpcServiceAttribute(implementation));
    }

    private static async Task<Document> AddIServiceAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var declaration = await FindNodeAsync<InterfaceDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (declaration is null || semanticModel is null)
            return document;

        var serviceType = semanticModel.Compilation.GetTypeByMetadataName("SharpLink.Sdk.IService");
        var visibleServices = semanticModel.LookupNamespacesAndTypes(
            declaration.SpanStart, name: "IService").OfType<INamedTypeSymbol>().ToArray();
        var serviceName = serviceType is not null && visibleServices.Length == 1 &&
                          SymbolEqualityComparer.Default.Equals(serviceType, visibleServices[0])
            ? "IService"
            : "global::SharpLink.Sdk.IService";
        var baseType = SyntaxFactory.SimpleBaseType(
            SyntaxFactory.ParseTypeName(serviceName));
        var baseList = declaration.BaseList is null
            ? SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType))
            : declaration.BaseList.WithTypes(declaration.BaseList.Types.Add(baseType));
        return await ReplaceNodeAsync(
            document,
            declaration,
            declaration.WithBaseList(baseList).WithAdditionalAnnotations(Formatter.Annotation),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Solution> AddNonCancellableAsync(
        Solution solution,
        ImmutableArray<IMethodSymbol> methods,
        CancellationToken cancellationToken)
    {
        var referencesByDocument = new Dictionary<
            DocumentId,
            List<Microsoft.CodeAnalysis.Text.TextSpan>>();
        foreach (var method in methods.Where(static method => !HasNonCancellableAttribute(method)))
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                    continue;
                if (!referencesByDocument.TryGetValue(document.Id, out var spans))
                {
                    spans = [];
                    referencesByDocument.Add(document.Id, spans);
                }
                if (!spans.Contains(reference.Span))
                    spans.Add(reference.Span);
            }
        }

        foreach (var pair in referencesByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            var root = document is null
                ? null
                : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                continue;
            var declarations = pair.Value
                .Select(span => root.FindNode(span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault())
                .Where(static declaration => declaration is not null)
                .Select(static declaration => declaration!)
                .Distinct()
                .ToArray();
            var updatedRoot = root.ReplaceNodes(declarations, static (_, current) =>
                current.AddAttributeLists(CreateAttributeList("global::SharpLink.Sdk.NonCancellable"))
                    .WithAdditionalAnnotations(Formatter.Annotation));
            solution = solution.WithDocumentSyntaxRoot(pair.Key, updatedRoot);
        }
        return solution;
    }

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
        var updated = attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(attribute, updated));
    }
}
