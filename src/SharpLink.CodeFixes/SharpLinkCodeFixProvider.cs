namespace SharpLink.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SharpLinkCodeFixProvider)), Shared]
internal sealed partial class SharpLinkCodeFixProvider : CodeFixProvider
{
    private const string SignatureKeyPrefix = "Signature:";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
    [
        "SHARPLINK002", "SHARPLINK004", "SHARPLINK006", "SHARPLINK007", "SHARPLINK008",
        "SHARPLINK009", "SHARPLINK014", "SHARPLINK015", "SHARPLINK016", "SHARPLINK018",
        "SHARPLINK019", "SHARPLINK020", "SHARPLINK028", "SHARPLINK031", "SHARPLINK032",
        "SHARPLINK033", "SHARPLINK037", "SHARPLINK043", "SHARPLINK049", "SHARPLINK050",
        "SHARPLINK051", "SHARPLINK053", "SHARPLINK055", "SHARPLINK056"
    ];

    public override FixAllProvider GetFixAllProvider() => SharpLinkFixAllProvider.Instance;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Location == Location.None || !diagnostic.Location.IsInSource)
                continue;

            await RegisterCodeFixesForDiagnosticAsync(context, diagnostic).ConfigureAwait(false);
        }
    }

    private static async Task RegisterCodeFixesForDiagnosticAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        switch (diagnostic.Id)
        {
            case "SHARPLINK002":
                await RegisterKeepParameterFixesAsync(context, diagnostic, ControlParameterKind.CancellationToken)
                    .ConfigureAwait(false);
                break;
            case "SHARPLINK004":
            case "SHARPLINK014":
                await RegisterCancellationContractFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK006":
                RegisterDocumentFix(context, diagnostic, "Add IService to RPC contract", "AddIService",
                    AddIServiceAsync);
                break;
            case "SHARPLINK007":
                await RegisterKeepParameterFixesAsync(context, diagnostic, ControlParameterKind.CallOptions)
                    .ConfigureAwait(false);
                break;
            case "SHARPLINK008":
                await RegisterReorderControlParametersFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK009":
                await RegisterDtoFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK015":
                await RegisterRemoveNonCancellableFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK016":
                await RegisterMissingContractFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK018":
                await RegisterServiceTypeFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK019":
                await RegisterConstructorFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK020":
                await RegisterServiceLifetimeFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK028":
                if (diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.PreviousMemberId, out var memberId) &&
                    uint.TryParse(memberId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMemberId) &&
                    parsedMemberId is > 0 and <= 536_870_911 &&
                    await CanRestoreMemberIdAsync(
                        context.Document, diagnostic, parsedMemberId, context.CancellationToken)
                        .ConfigureAwait(false))
                {
                    RegisterDocumentFix(context, diagnostic, $"Preserve published member ID {memberId}",
                        "RestoreMemberId",
                        (document, item, ct) => RestoreMemberIdAsync(document, item, memberId!, ct));
                }
                break;
            case "SHARPLINK031":
                if (diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.FixKind, out var requiredKind) &&
                    string.Equals(requiredKind, "RemoveRpcRequired", StringComparison.Ordinal) &&
                    await FindAttributeAtDiagnosticAsync(
                            context.Document,
                            diagnostic,
                            ["SharpLink.Sdk.RpcRequiredAttribute"],
                            context.CancellationToken)
                        .ConfigureAwait(false) is not null)
                {
                    RegisterDocumentFix(context, diagnostic, "Remove [RpcRequired]", "RemoveRpcRequired",
                        (document, item, ct) => RemoveAttributeAsync(
                            document, item, "SharpLink.Sdk.RpcRequiredAttribute", ct));
                }
                break;
            case "SHARPLINK032":
                if (diagnostic.Properties.TryGetValue(
                        SharpLinkDiagnosticProperties.PreviousEnumUnderlyingType, out var underlyingType) &&
                    !string.IsNullOrWhiteSpace(underlyingType) &&
                    TryGetEnumUnderlyingTypeSyntax(underlyingType!, out _) &&
                    await CanRestoreEnumUnderlyingTypeAsync(
                        context.Document, diagnostic, underlyingType!, context.CancellationToken)
                        .ConfigureAwait(false))
                {
                    RegisterDocumentFix(context, diagnostic, $"Restore published enum underlying type {underlyingType}",
                        "RestoreEnumType",
                        (document, item, ct) => RestoreEnumUnderlyingTypeAsync(
                            document, item, underlyingType!, ct));
                }
                break;
            case "SHARPLINK033":
                await RegisterUnionTagFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK037":
                await RegisterRestoreServiceRouteFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK043":
                await RegisterAdapterShapeFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK049":
                RegisterDocumentFix(context, diagnostic, "Remove built-in Codec adapter binding",
                    "RemoveBuiltinAdapterBinding", RemoveContainingAttributeAsync);
                break;
            case "SHARPLINK050":
                await RegisterTimeoutFixesAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK051":
                RegisterDocumentFix(context, diagnostic, "Remove invalid RPC union case mapping",
                    "RemoveInvalidUnionCase", RemoveContainingAttributeAsync);
                break;
            case "SHARPLINK053":
                await RegisterMakeInstanceMethodFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK055":
                await RegisterMakePublicFixAsync(
                    context,
                    diagnostic,
                    "Make RPC contract publicly reachable",
                    "MakeContractPublic").ConfigureAwait(false);
                break;
            case "SHARPLINK056":
                await RegisterRemoveOnewayFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
        }
    }

    private static void RegisterDocumentFix(
        CodeFixContext context,
        Diagnostic diagnostic,
        string title,
        string equivalenceKey,
        Func<Document, Diagnostic, CancellationToken, Task<Document>> apply)
        => context.RegisterCodeFix(
            CodeAction.Create(
                title,
                async cancellationToken =>
                    (await apply(context.Document, diagnostic, cancellationToken).ConfigureAwait(false))
                    .Project.Solution,
                equivalenceKey),
            diagnostic);

    private static void RegisterSolutionFix(
        CodeFixContext context,
        Diagnostic diagnostic,
        string title,
        string equivalenceKey,
        Func<Solution, DocumentId, Diagnostic, CancellationToken, Task<Solution>> apply)
        => context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancellationToken => apply(
                    context.Document.Project.Solution,
                    context.Document.Id,
                    diagnostic,
                    cancellationToken),
                equivalenceKey),
            diagnostic);

    private static async Task RegisterMakePublicFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic,
        string title,
        string equivalenceKey)
    {
        var declaration = await FindNodeAsync<BaseTypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol type ||
            !TryGetPublicizationClosure(type, out _))
        {
            return;
        }

        RegisterSolutionFix(
            context,
            diagnostic,
            title,
            equivalenceKey,
            MakeContainingTypesPublicAcrossSolutionAsync);
    }

    private static async Task RegisterCancellationContractFixesAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var method = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        if (method is null)
            return;

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var symbol = semanticModel?.GetDeclaredSymbol(method, context.CancellationToken);
        if (symbol is null)
            return;

        var canAddCancellationToken = await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
            .ConfigureAwait(false);
        if (canAddCancellationToken)
        {
            var relatedMethods = await FindRelatedMethodsAsync(
                symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
            canAddCancellationToken = !relatedMethods.Any(static related =>
                                          related.Parameters.Any(static parameter =>
                                              parameter.IsOptional || parameter.IsParams)) &&
                                      CanApplySignatureEditWithoutCollisions(
                                          relatedMethods,
                                          new SignatureEditPlan(SignatureEditKind.AddCancellationToken));
        }

        if (canAddCancellationToken)
        {
            RegisterSolutionFix(context, diagnostic, "Add CancellationToken",
                SignatureKeyPrefix + "AddCancellationToken", AddCancellationTokenAsync);
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        if (equivalentMethods.Length != 0 &&
            equivalentMethods.All(static candidate => candidate.DeclaringSyntaxReferences.Length != 0))
        {
            RegisterSolutionFix(context, diagnostic, "Annotate with [NonCancellable]", "AddNonCancellable",
                (solution, _, _, ct) => AddNonCancellableAsync(solution, equivalentMethods, ct));
        }
    }

    private static async Task RegisterRemoveNonCancellableFixAsync(
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
        var attributedMethods = equivalentMethods.Where(HasNonCancellableAttribute).ToImmutableArray();
        if (attributedMethods.Length == 0 ||
            attributedMethods.Any(static candidate => candidate.DeclaringSyntaxReferences.Length == 0))
        {
            return;
        }

        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [NonCancellable]",
            "RemoveNonCancellable",
            (solution, _, _, ct) => RemoveNonCancellableAsync(solution, attributedMethods, ct));
    }

    private static async Task RegisterKeepParameterFixesAsync(
        CodeFixContext context,
        Diagnostic diagnostic,
        ControlParameterKind kind)
    {
        var method = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (method is null || semanticModel?.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol)
            return;
        if (!await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        var relatedMethods = await FindRelatedMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        foreach (var parameter in symbol.Parameters.Where(parameter => IsControlParameter(parameter, kind)))
        {
            var ordinal = parameter.Ordinal;
            if (!CanApplySignatureEditWithoutCollisions(
                    relatedMethods,
                    new SignatureEditPlan(SignatureEditKind.KeepControlParameter, kind, ordinal)) ||
                !CanRemoveControlParametersWithoutBreakingNameReferences(relatedMethods, kind, ordinal))
            {
                continue;
            }
            var displayKind = kind == ControlParameterKind.CancellationToken
                ? "CancellationToken"
                : "SharpLinkCallOptions";
            RegisterSolutionFix(
                context,
                diagnostic,
                $"Keep {displayKind} '{parameter.Name}'",
                SignatureKeyPrefix + "Keep:" + kind + ":" + ordinal.ToString(CultureInfo.InvariantCulture),
                (solution, documentId, item, ct) => KeepControlParameterAsync(
                    solution, documentId, item, kind, ordinal, ct));
        }
    }

    private static async Task RegisterReorderControlParametersFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var method = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (method is null || semanticModel?.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol ||
            symbol.Parameters.Count(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CancellationToken)) > 1 ||
            symbol.Parameters.Count(parameter =>
                IsControlParameter(parameter, ControlParameterKind.CallOptions)) > 1 ||
            !await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var relatedMethods = await FindRelatedMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        if (relatedMethods.Any(static related =>
                related.Parameters.Any(static parameter => parameter.IsOptional || parameter.IsParams)) ||
            !CanApplySignatureEditWithoutCollisions(
                relatedMethods,
                new SignatureEditPlan(SignatureEditKind.ReorderControlParameters)) ||
            !CanReorderControlParametersWithoutBreakingHandlerDependencies(relatedMethods))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, "Reorder RPC control parameters",
            SignatureKeyPrefix + "ReorderControlParameters", ReorderControlParametersAsync);
    }

    private static async Task RegisterDtoFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.FixKind, out var fixKind))
            return;

        if (string.Equals(fixKind, "SealDto", StringComparison.Ordinal))
        {
            var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
                context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
                .ConfigureAwait(false);
            if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type)
                return;
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType ||
                type.BaseType?.SpecialType != SpecialType.System_Object ||
                type.GetMembers().Any(static member =>
                    !member.IsImplicitlyDeclared && member.IsVirtual && !member.IsOverride))
            {
                return;
            }
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                type, context.Document.Project.Solution, cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
            if (!derived.Any(static item => item.Locations.Any(static location => location.IsInSource)))
            {
                RegisterDocumentFix(context, diagnostic, "Seal DTO for generated Codec", "SealDto",
                    (document, item, ct) => UpdateTypeAtDiagnosticAsync(
                        document, item, static node => AddModifier(RemoveModifier(node, SyntaxKind.AbstractKeyword),
                            SyntaxKind.SealedKeyword), ct));
            }
        }
        else if (string.Equals(fixKind, "MakeDtoAccessible", StringComparison.Ordinal))
        {
            await RegisterMakePublicFixAsync(
                context,
                diagnostic,
                "Make DTO publicly reachable",
                "MakeDtoAccessible").ConfigureAwait(false);
        }
    }

    private static async Task RegisterMissingContractFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } service)
            return;

        var candidates = service.AllInterfaces
            .Where(static item => item.Locations.Any(static location => location.IsInSource))
            .Where(static item => item.AllInterfaces.Any(IsIService))
            .Where(static item => !HasAttribute(item, "SharpLink.Sdk.RpcContractAttribute"))
            .ToArray();
        if (candidates.Length != 1 || candidates[0].DeclaringSyntaxReferences.Length == 0 ||
            !IsEffectivelyPublic(candidates[0]))
            return;

        var candidate = candidates[0];
        var implementations = await SymbolFinder.FindImplementationsAsync(
            candidate,
            context.Document.Project.Solution,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        if (implementations.OfType<INamedTypeSymbol>().Any(implementation =>
                implementation.TypeKind == TypeKind.Class &&
                !SymbolEqualityComparer.Default.Equals(implementation, service) &&
                HasAttribute(implementation, "SharpLink.Sdk.RpcServiceAttribute") &&
                implementation.AllInterfaces.Any(HasRpcContractAttribute)))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, $"Annotate {candidate.Name} with [RpcContract]",
            "AnnotateRpcContract",
            (solution, _, _, ct) => AddAttributeToSymbolAsync(
                solution, candidate, "global::SharpLink.Sdk.RpcContract", ct));
    }

    private static async Task RegisterServiceTypeFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type ||
            type.TypeKind != TypeKind.Class || type.IsGenericType)
        {
            return;
        }

        var canMakePublic = TryGetPublicizationClosure(type, out _);
        if (!IsEffectivelyPublic(type) && !type.IsAbstract && canMakePublic)
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service publicly reachable",
                "MakeServicePublic", MakeContainingTypesPublicAcrossSolutionAsync);
        }

        if (type.IsAbstract && IsEffectivelyPublic(type) && IsSafeToMakeConcrete(type))
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service concrete", "MakeServiceConcrete",
                (solution, _, _, ct) => FixServiceShapeAcrossSolutionAsync(
                    solution, type, makePublic: false, ct));
        }
        else if (type.IsAbstract && !IsEffectivelyPublic(type) && IsSafeToMakeConcrete(type) && canMakePublic)
        {
            RegisterSolutionFix(context, diagnostic,
                "Make RPC service concrete and publicly reachable",
                "MakeServiceConcreteAndPublic",
                (solution, _, _, ct) => FixServiceShapeAcrossSolutionAsync(
                    solution, type, makePublic: true, ct));
        }
    }

    private static async Task RegisterConstructorFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type ||
            type.TypeKind != TypeKind.Class)
            return;

        var allPublicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var publicConstructors = allPublicConstructors
            .Where(IsSupportedServiceConstructor)
            .Where(constructor => ConstructorSatisfiesRequiredMembers(type, constructor))
            .ToArray();
        var nonPublicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility != Accessibility.Public)
            .Where(IsSupportedServiceConstructor)
            .Where(constructor => ConstructorSatisfiesRequiredMembers(type, constructor))
            .ToArray();

        if (allPublicConstructors.Length == 0 && nonPublicConstructors.Length == 1 &&
            CanExposeAsPublic(nonPublicConstructors[0]))
        {
            var constructor = nonPublicConstructors[0];
            RegisterSolutionFix(context, diagnostic, $"Make {type.Name} constructor public",
                "MakeConstructorPublic",
                (solution, _, _, ct) => MakeConstructorPublicAsync(solution, constructor, ct));
            return;
        }

        var marker = semanticModel.Compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute");
        var alreadyMarked = allPublicConstructors.Any(static constructor => constructor.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute",
                StringComparison.Ordinal)));
        if (allPublicConstructors.Length <= 1 || publicConstructors.Length == 0 ||
            marker is null || alreadyMarked)
            return;

        foreach (var constructor in publicConstructors)
        {
            var selected = constructor;
            var signature = string.Join(", ", constructor.Parameters.Select(static item =>
                item.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            RegisterSolutionFix(context, diagnostic, $"Select constructor {type.Name}({signature})",
                "SelectConstructor:" + constructor.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                (solution, _, _, ct) => AddAttributeToSymbolAsync(
                    solution,
                    selected,
                    "global::Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor",
                    ct));
        }
    }

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
        if (service?.TypeKind != TypeKind.Class)
            return;
        var attributeReference = service?.GetAttributes()
            .FirstOrDefault(static attribute => string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "SharpLink.Sdk.RpcServiceAttribute",
                StringComparison.Ordinal))
            ?.ApplicationSyntaxReference;
        if (attributeReference is null)
            return;

        foreach (var lifetime in new[] { "Singleton", "Connection", "Call" })
        {
            var value = lifetime;
            RegisterSolutionFix(context, diagnostic, $"Set RPC service lifetime to {value}",
                "SetLifetime:" + value,
                (solution, _, _, ct) => SetServiceLifetimeAsync(
                    solution, attributeReference, value, ct));
        }
    }

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
            namedCase.IsUnboundGenericType || ContainsTypeParameter(namedCase) ||
            namedCase.TypeKind is not (TypeKind.Class or TypeKind.Struct) || namedCase.IsAbstract ||
            semanticModel.Compilation is not CSharpCompilation csharpCompilation ||
            !csharpCompilation.ClassifyConversion(namedCase, unionType).IsImplicit)
            return;

        if (unionType.GetAttributes().Where(IsRpcUnionCaseAttribute).Any(attribute =>
                attribute.ConstructorArguments.Length == 2 &&
                attribute.ConstructorArguments[1].Value is ITypeSymbol existingCase &&
                SymbolEqualityComparer.Default.Equals(existingCase, namedCase) &&
                (attribute.ApplicationSyntaxReference is not { } reference ||
                 reference.SyntaxTree != targetAttribute.SyntaxTree ||
                 reference.Span != targetAttribute.Span)))
        {
            return;
        }

        RegisterDocumentFix(context, diagnostic, $"Restore tag {tag} to {type}",
            "RestoreUnionTag",
            (document, item, ct) => RestoreUnionTagAsync(document, item, tag!, type!, ct));
    }

    private static async Task RegisterRestoreServiceRouteFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<InterfaceDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } contract)
            return;

        var implementations = await SymbolFinder.FindImplementationsAsync(
            contract, context.Document.Project.Solution, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        var candidates = implementations
            .OfType<INamedTypeSymbol>()
            .Where(static item => item.TypeKind == TypeKind.Class && !item.IsAbstract && !item.IsGenericType)
            .Where(static item => item.Locations.Any(static location => location.IsInSource))
            .Where(IsEffectivelyPublic)
            .Where(static item => !HasAttribute(item, "SharpLink.Sdk.RpcServiceAttribute"))
            .Where(static item => item.AllInterfaces.Count(candidate =>
                HasAttribute(candidate, "SharpLink.Sdk.RpcContractAttribute")) == 1)
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
            adapter.GetMembers().Any(static member =>
                !member.IsImplicitlyDeclared && member.IsVirtual && !member.IsOverride) ||
            !CanExposePublicParameterlessConstructor(adapter) ||
            !CanCallParameterlessConstructorWithRequiredMembers(adapter) ||
            HasPrimaryConstructorWithoutParameterlessAlternative(adapter, context.CancellationToken) ||
            !TryGetPublicizationClosure(adapter, out _) ||
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
            !symbol.IsStatic || symbol.MethodKind != MethodKind.Ordinary ||
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

    private static async Task<Solution> RemoveNonCancellableAsync(
        Solution solution,
        ImmutableArray<IMethodSymbol> methods,
        CancellationToken cancellationToken)
    {
        var referencesByDocument = new Dictionary<
            DocumentId,
            HashSet<Microsoft.CodeAnalysis.Text.TextSpan>>();
        foreach (var reference in methods
                     .SelectMany(static method => method.GetAttributes())
                     .Where(IsNonCancellableAttribute)
                     .Select(static attribute => attribute.ApplicationSyntaxReference)
                     .Where(static reference => reference is not null)
                     .Select(static reference => reference!))
        {
            var document = solution.GetDocument(reference.SyntaxTree);
            if (document is null)
                continue;
            if (!referencesByDocument.TryGetValue(document.Id, out var spans))
            {
                spans = [];
                referencesByDocument.Add(document.Id, spans);
            }
            spans.Add(reference.Span);
        }

        foreach (var pair in referencesByDocument)
        {
            var document = solution.GetDocument(pair.Key);
            var root = document is null
                ? null
                : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                continue;

            foreach (var span in pair.Value.OrderByDescending(static span => span.Start))
            {
                var attribute = root.FindNode(span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
                if (attribute?.Parent is not AttributeListSyntax list)
                    continue;
                root = list.Attributes.Count == 1
                    ? root.RemoveNode(list, SyntaxRemoveOptions.KeepExteriorTrivia) ?? root
                    : root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
            }
            solution = solution.WithDocumentSyntaxRoot(pair.Key, root);
        }
        return solution;
    }

    private static async Task<Solution> SetServiceLifetimeAsync(
        Solution solution,
        SyntaxReference attributeReference,
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

        var value = SyntaxFactory.ParseExpression(
            "global::SharpLink.Sdk.SharpLinkServiceLifetime." + lifetime);
        var argument = SyntaxFactory.AttributeArgument(value)
            .WithNameEquals(SyntaxFactory.NameEquals("Lifetime"));
        var arguments = attribute.ArgumentList?.Arguments ?? default;
        var existing = arguments.FirstOrDefault(static item => item.NameEquals?.Name.Identifier.ValueText == "Lifetime");
        var updatedArguments = existing is null ? arguments.Add(argument) : arguments.Replace(existing, argument);
        var updated = attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(attribute, updated));
    }

    private static async Task<Document> RestoreMemberIdAsync(
        Document document,
        Diagnostic diagnostic,
        string memberId,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var member = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(static item => item is PropertyDeclarationSyntax or FieldDeclarationSyntax);
        if (member is null || semanticModel is null)
            return document;

        var attribute = member.AttributeLists.SelectMany(static list => list.Attributes)
            .FirstOrDefault(item => AttributeMatches(
                semanticModel, item, "SharpLink.Sdk.RpcMemberAttribute", cancellationToken));
        var argument = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(memberId));
        if (attribute is not null)
        {
            var updated = attribute.WithArgumentList(
                    SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(argument)))
                .WithAdditionalAnnotations(Formatter.Annotation);
            return await ReplaceNodeAsync(document, attribute, updated, cancellationToken).ConfigureAwait(false);
        }

        var attributeList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::SharpLink.Sdk.RpcMember"))
                    .WithArgumentList(SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(argument)))));
        var updatedMember = member switch
        {
            PropertyDeclarationSyntax property => property.AddAttributeLists(attributeList),
            FieldDeclarationSyntax field => field.AddAttributeLists(attributeList),
            _ => member
        };
        return await ReplaceNodeAsync(
            document, member, updatedMember.WithAdditionalAnnotations(Formatter.Annotation), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> CanRestoreMemberIdAsync(
        Document document,
        Diagnostic diagnostic,
        uint memberId,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var member = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(static item => item is PropertyDeclarationSyntax or FieldDeclarationSyntax);
        var validDeclaration = member switch
        {
            PropertyDeclarationSyntax => true,
            FieldDeclarationSyntax field => field.Declaration.Variables.Count == 1,
            _ => false
        };
        if (!validDeclaration || semanticModel is null)
            return false;

        ISymbol? target = member switch
        {
            PropertyDeclarationSyntax property => semanticModel.GetDeclaredSymbol(property, cancellationToken),
            FieldDeclarationSyntax field => semanticModel.GetDeclaredSymbol(
                field.Declaration.Variables[0], cancellationToken),
            _ => null
        };
        if (target?.ContainingType is not { } containingType)
            return false;

        return !containingType.GetMembers()
            .Where(IsSerializableRpcMember)
            .Where(candidate => !SymbolEqualityComparer.Default.Equals(candidate, target))
            .Any(candidate => TryGetRpcMemberId(candidate, out var candidateId) && candidateId == memberId);
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
            .ToImmutableArray();
        if (timeoutAttributes.Length == 0 ||
            timeoutAttributes.Any(static attribute => attribute.ApplicationSyntaxReference is null))
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
            .SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsOnewayAttribute)
            .ToImmutableArray();
        if (attributes.Length == 0 || attributes.Any(static attribute => attribute.ApplicationSyntaxReference is null))
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
                    : root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
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

    private static async Task<Document> RemoveAttributeAsync(
        Document document,
        Diagnostic diagnostic,
        string metadataName,
        CancellationToken cancellationToken)
        => await RemoveAttributeAsync(document, diagnostic, [metadataName], cancellationToken)
            .ConfigureAwait(false);

    private static async Task<Document> RemoveAttributeAsync(
        Document document,
        Diagnostic diagnostic,
        IReadOnlyCollection<string> metadataNames,
        CancellationToken cancellationToken)
    {
        var attribute = await FindAttributeAtDiagnosticAsync(
            document, diagnostic, metadataNames, cancellationToken).ConfigureAwait(false);
        return attribute is null
            ? document
            : await RemoveAttributeNodeAsync(document, attribute, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AttributeSyntax?> FindAttributeAtDiagnosticAsync(
        Document document,
        Diagnostic diagnostic,
        IReadOnlyCollection<string> metadataNames,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
            return null;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var attribute = node.AncestorsAndSelf().SelectMany(static item => item.ChildNodes().OfType<AttributeListSyntax>())
            .SelectMany(static list => list.Attributes)
            .FirstOrDefault(item => metadataNames.Any(metadataName =>
                AttributeMatches(semanticModel, item, metadataName, cancellationToken)));
        return attribute ?? node.AncestorsAndSelf().OfType<AttributeSyntax>()
            .FirstOrDefault(item => metadataNames.Any(metadataName =>
                AttributeMatches(semanticModel, item, metadataName, cancellationToken)));
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
            updatedRoot = root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
        }
        return document.WithSyntaxRoot(updatedRoot);
    }

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

        if (!TryGetPublicizationClosure(symbol, out var publicizedTypes))
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
                .Select(span => root.FindNode(span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault())
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
            if (!TryGetPublicizationClosure(type, out typesToUpdate))
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
            var declarations = new Dictionary<BaseTypeDeclarationSyntax, bool>();
            foreach (var item in pair.Value)
            {
                var declaration = root.FindNode(item.Key, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                if (declaration is not null)
                    declarations[declaration] = item.Value;
            }
            var updatedRoot = root.ReplaceNodes(declarations.Keys, (original, current) =>
            {
                BaseTypeDeclarationSyntax updated = current;
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
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
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

    private static async Task<Solution> MakeConstructorPublicAsync(
        Solution solution,
        IMethodSymbol constructor,
        CancellationToken cancellationToken)
    {
        var reference = constructor.DeclaringSyntaxReferences.FirstOrDefault();
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
        if (!TryGetPublicizationClosure(adapter, out var publicizedTypes))
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

            var declarations = new Dictionary<BaseTypeDeclarationSyntax, bool>();
            foreach (var item in pair.Value)
            {
                var declaration = root.FindNode(item.Key, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
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

    private static async Task<TNode?> FindNodeAsync<TNode>(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
        where TNode : SyntaxNode
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<TNode>().FirstOrDefault();
    }

    private static async Task<Document> ReplaceNodeAsync(
        Document document,
        SyntaxNode oldNode,
        SyntaxNode newNode,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null ? document : document.WithSyntaxRoot(root.ReplaceNode(oldNode, newNode));
    }

    private static AttributeListSyntax CreateAttributeList(string attributeName)
        => SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.ParseName(attributeName))));

    private static bool AttributeMatches(
        SemanticModel semanticModel,
        AttributeSyntax attribute,
        string metadataName,
        CancellationToken cancellationToken)
        => semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is IMethodSymbol constructor &&
           string.Equals(constructor.ContainingType.ToDisplayString(), metadataName, StringComparison.Ordinal);

    private static bool HasAttribute(ISymbol symbol, string metadataName)
        => symbol.GetAttributes().Any(item =>
            string.Equals(item.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private static bool IsTimeoutAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.TimeoutAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.TimeoutAttribute",
               StringComparison.Ordinal);

    private static bool IsOnewayAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.OnewayAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.OnewayAttribute",
               StringComparison.Ordinal);

    private static bool IsRpcUnionCaseAttribute(AttributeData attribute)
        => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            "SharpLink.Sdk.RpcUnionCaseAttribute",
            StringComparison.Ordinal);

    private static bool IsSerializableRpcMember(ISymbol member)
        => !member.IsStatic && member.DeclaredAccessibility == Accessibility.Public &&
           !HasAttribute(member, "SharpLink.Sdk.RpcIgnoreAttribute") &&
           (member is IFieldSymbol { IsConst: false } ||
            member is IPropertySymbol
            {
                IsIndexer: false,
                GetMethod.DeclaredAccessibility: Accessibility.Public
            });

    private static bool TryGetRpcMemberId(ISymbol member, out uint id)
    {
        var attribute = member.GetAttributes().FirstOrDefault(item => string.Equals(
            item.AttributeClass?.ToDisplayString(),
            "SharpLink.Sdk.RpcMemberAttribute",
            StringComparison.Ordinal));
        if (attribute is not null)
        {
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int explicitId &&
                explicitId is > 0 and <= 0x1FFF_FFFF)
            {
                id = (uint)explicitId;
                return true;
            }
            id = 0;
            return false;
        }

        var hash = 2166136261U;
        foreach (var character in member.Name)
        {
            hash ^= character;
            hash *= 16777619U;
        }
        id = hash & 0x1FFF_FFFFU;
        if (id == 0)
            id = 1;
        return true;
    }

    private static bool HasNonCancellableAttribute(IMethodSymbol method)
        => method.GetAttributes().Any(IsNonCancellableAttribute);

    private static bool IsNonCancellableAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.NonCancellableAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.NonCancellableAttribute",
               StringComparison.Ordinal);

    private static async Task<ImmutableArray<IMethodSymbol>> FindEquivalentInterfaceMethodsAsync(
        IMethodSymbol method,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface)
            return ImmutableArray.Create(method);

        var contractTypes = new List<INamedTypeSymbol>();
        if (HasRpcContractAttribute(method.ContainingType))
            contractTypes.Add(method.ContainingType);

        var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(
            method.ContainingType,
            solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var derived in derivedInterfaces.Where(HasRpcContractAttribute))
        {
            if (!contractTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, derived)))
                contractTypes.Add(derived);
        }

        if (contractTypes.Count == 0)
            contractTypes.Add(method.ContainingType);

        var methods = new List<IMethodSymbol>();
        foreach (var contract in contractTypes)
        {
            foreach (var @interface in new[] { contract }.Concat(contract.AllInterfaces)
                         .Where(static candidate => !IsIService(candidate)))
            {
                foreach (var candidate in @interface.GetMembers(method.Name).OfType<IMethodSymbol>()
                             .Where(static candidate => candidate.MethodKind == MethodKind.Ordinary &&
                                                        candidate.DeclaredAccessibility == Accessibility.Public))
                {
                    if (!HasEquivalentContractSignature(method, candidate) ||
                        methods.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                    {
                        continue;
                    }
                    methods.Add(candidate);
                }
            }
        }
        return methods.ToImmutableArray();
    }

    private static bool HasRpcContractAttribute(INamedTypeSymbol type)
        => HasAttribute(type, "SharpLink.Sdk.RpcContractAttribute") ||
           HasAttribute(type, "SharpLink.Abstractions.RpcContractAttribute");

    private static bool HasEquivalentContractSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arity != right.Arity || left.Parameters.Length != right.Parameters.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Parameters.Length; index++)
        {
            if (left.Parameters[index].RefKind != right.Parameters[index].RefKind ||
                !SymbolEqualityComparer.Default.Equals(
                    left.Parameters[index].Type,
                    right.Parameters[index].Type))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsIService(INamedTypeSymbol type)
        => type.Name == "IService" && type.ContainingNamespace.ToDisplayString() == "SharpLink.Sdk";

    private static bool IsCodecAdapter(INamedTypeSymbol type)
        => type.Name == "IRpcCodecAdapter" &&
           type.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions";

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type.TypeKind == TypeKind.TypeParameter ||
           type is INamedTypeSymbol named &&
           (named.TypeArguments.Any(ContainsTypeParameter) ||
            named.ContainingType is not null && ContainsTypeParameter(named.ContainingType));

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }

    private static bool TryGetPublicizationClosure(
        INamedTypeSymbol root,
        out ImmutableArray<INamedTypeSymbol> types)
    {
        var result = new List<INamedTypeSymbol>();
        var pending = new Queue<INamedTypeSymbol>();
        Add(root.OriginalDefinition);

        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            if (HasFileLocalNameCollision(current))
            {
                types = default;
                return false;
            }
            if (current.ContainingType is { } containing)
                Add(containing.OriginalDefinition);
            if (current.BaseType is { } baseType && !AddAccessibilityDependency(baseType))
            {
                types = default;
                return false;
            }
            foreach (var @interface in current.Interfaces)
            {
                if (!AddAccessibilityDependency(@interface))
                {
                    types = default;
                    return false;
                }
            }
            foreach (var typeParameter in current.TypeParameters)
            {
                foreach (var constraint in typeParameter.ConstraintTypes)
                {
                    if (!AddAccessibilityDependency(constraint))
                    {
                        types = default;
                        return false;
                    }
                }
            }
            foreach (var member in current.GetMembers().Where(static member =>
                         member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or
                             Accessibility.ProtectedOrInternal))
            {
                if (!AddMemberAccessibilityDependencies(member))
                {
                    types = default;
                    return false;
                }
            }
        }

        types = result.ToImmutableArray();
        return true;

        void Add(INamedTypeSymbol candidate)
        {
            if (result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                return;
            result.Add(candidate);
            pending.Enqueue(candidate);
        }

        bool AddAccessibilityDependency(ITypeSymbol dependency)
        {
            switch (dependency)
            {
                case IArrayTypeSymbol array:
                    return AddAccessibilityDependency(array.ElementType);
                case IPointerTypeSymbol pointer:
                    return AddAccessibilityDependency(pointer.PointedAtType);
                case IFunctionPointerTypeSymbol functionPointer:
                    return AddAccessibilityDependency(functionPointer.Signature.ReturnType) &&
                           functionPointer.Signature.Parameters.All(parameter =>
                               AddAccessibilityDependency(parameter.Type));
                case IErrorTypeSymbol:
                    return false;
                case INamedTypeSymbol named:
                    {
                        var definition = named.OriginalDefinition;
                        if (!IsEffectivelyPublic(definition))
                        {
                            if (definition.DeclaringSyntaxReferences.Length == 0 ||
                                definition.DeclaringSyntaxReferences.Any(static reference =>
                                    reference.GetSyntax() is not BaseTypeDeclarationSyntax))
                            {
                                return false;
                            }
                            Add(definition);
                        }
                        if (named.ContainingType is { } containingType &&
                            !AddAccessibilityDependency(containingType))
                        {
                            return false;
                        }
                        foreach (var argument in named.TypeArguments)
                        {
                            if (!AddAccessibilityDependency(argument))
                                return false;
                        }
                        return true;
                    }
                case ITypeParameterSymbol:
                case IDynamicTypeSymbol:
                    return true;
                default:
                    return true;
            }
        }

        bool AddMemberAccessibilityDependencies(ISymbol member)
        {
            switch (member)
            {
                case INamedTypeSymbol nestedType:
                    return AddAccessibilityDependency(nestedType);
                case IFieldSymbol field:
                    return AddAccessibilityDependency(field.Type);
                case IEventSymbol @event:
                    return AddAccessibilityDependency(@event.Type);
                case IPropertySymbol property:
                    return AddAccessibilityDependency(property.Type) &&
                           property.Parameters.All(parameter =>
                               AddAccessibilityDependency(parameter.Type));
                case IMethodSymbol method:
                    if (!AddAccessibilityDependency(method.ReturnType) ||
                        !method.Parameters.All(parameter =>
                            AddAccessibilityDependency(parameter.Type)))
                    {
                        return false;
                    }
                    return method.TypeParameters.All(typeParameter =>
                        typeParameter.ConstraintTypes.All(AddAccessibilityDependency));
                default:
                    return true;
            }
        }
    }

    private static bool HasFileLocalNameCollision(INamedTypeSymbol type)
    {
        var isFileLocal = type.DeclaringSyntaxReferences.Any(static reference =>
            reference.GetSyntax() is BaseTypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.FileKeyword));
        return isFileLocal && type.ContainingType is null &&
               type.ContainingNamespace.GetTypeMembers(type.Name, type.Arity).Any(candidate =>
                   !SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, type.OriginalDefinition));
    }

    private static bool IsSafeToMakeConcrete(INamedTypeSymbol type)
    {
        if (type.GetMembers().Any(static member => member.IsAbstract && !member.IsImplicitlyDeclared))
            return false;
        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (type.FindImplementationForInterfaceMember(member) is null)
                    return false;
            }
        }
        foreach (var baseType in GetBaseTypes(type))
        {
            foreach (var member in baseType.GetMembers().Where(static member => member.IsAbstract))
            {
                if (!HasOverride(type, member))
                    return false;
            }
        }
        return true;
    }

    private static IEnumerable<INamedTypeSymbol> GetBaseTypes(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            yield return current;
    }

    private static IEnumerable<INamedTypeSymbol> GetContainingTypes(INamedTypeSymbol type)
    {
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
            yield return current;
    }

    private static bool CanExposePublicParameterlessConstructor(INamedTypeSymbol type)
    {
        if (type.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0))
            return true;
        var baseType = type.BaseType;
        if (baseType is null)
            return true;
        return baseType.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 &&
            (constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or
                 Accessibility.ProtectedOrInternal ||
             constructor.DeclaredAccessibility == Accessibility.Internal &&
             SymbolEqualityComparer.Default.Equals(
                 constructor.ContainingAssembly, type.ContainingAssembly)));
    }

    private static bool CanCallParameterlessConstructorWithRequiredMembers(INamedTypeSymbol type)
    {
        if (!HasRequiredMembers(type))
            return true;
        var constructor = type.InstanceConstructors.FirstOrDefault(static candidate =>
            candidate.Parameters.Length == 0 && !candidate.IsStatic);
        return constructor is not null && HasAttribute(
            constructor,
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
    }

    private static bool HasRequiredMembers(INamedTypeSymbol type)
        => new[] { type }.Concat(GetBaseTypes(type))
            .SelectMany(static current => current.GetMembers())
            .Any(static member => member is IFieldSymbol { IsRequired: true } or
                IPropertySymbol { IsRequired: true });

    private static bool ConstructorSatisfiesRequiredMembers(
        INamedTypeSymbol type,
        IMethodSymbol constructor)
        => !HasRequiredMembers(type) ||
           HasAttribute(
               constructor,
               "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

    private static bool HasPrimaryConstructorWithoutParameterlessAlternative(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
        => !type.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0) &&
           type.DeclaringSyntaxReferences.Any(reference =>
               reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax
               {
                   ParameterList.Parameters.Count: > 0
               });

    private static bool HasOverride(INamedTypeSymbol type, ISymbol abstractMember)
    {
        foreach (var currentType in new[] { type }.Concat(GetBaseTypes(type)))
        {
            var overrides = currentType.GetMembers(abstractMember.Name)
                .Where(candidate => Overrides(candidate, abstractMember))
                .ToArray();
            if (overrides.Length != 0)
                return overrides.Any(static candidate => !candidate.IsAbstract);
        }
        return false;

        static bool Overrides(ISymbol candidate, ISymbol abstractMember)
        {
            for (var current = candidate; current is not null; current = GetOverriddenMember(current))
            {
                if (SymbolEqualityComparer.Default.Equals(current, abstractMember))
                    return true;
            }
            return false;
        }
    }

    private static ISymbol? GetOverriddenMember(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };

    private static bool IsSupportedServiceConstructor(IMethodSymbol constructor)
        => constructor.Parameters.All(static parameter =>
            parameter.RefKind is not (RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter) &&
            parameter.Type.TypeKind is not (TypeKind.Pointer or TypeKind.FunctionPointer) &&
            !parameter.Type.IsRefLikeType);

    private static bool CanExposeAsPublic(IMethodSymbol constructor)
        => constructor.Parameters.All(static parameter => IsPubliclyAccessible(parameter.Type));

    private static bool IsPubliclyAccessible(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => IsPubliclyAccessible(array.ElementType),
            IPointerTypeSymbol => false,
            IFunctionPointerTypeSymbol => false,
            ITypeParameterSymbol => false,
            IErrorTypeSymbol => false,
            INamedTypeSymbol named => IsEffectivelyPublic(named.OriginalDefinition) &&
                                     (named.ContainingType is null ||
                                      IsPubliclyAccessible(named.ContainingType)) &&
                                     named.TypeArguments.All(IsPubliclyAccessible),
            IDynamicTypeSymbol => true,
            _ => true
        };

    private static bool HasValidServiceActivationShape(INamedTypeSymbol service)
    {
        var constructors = service.InstanceConstructors
            .Where(static item => item.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var marked = constructors.Where(static constructor => constructor.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute",
                StringComparison.Ordinal))).ToArray();
        var selected = marked.Length == 1
            ? marked[0]
            : marked.Length == 0 && constructors.Length == 1 ? constructors[0] : null;
        return selected is not null && IsSupportedServiceConstructor(selected) &&
               ConstructorSatisfiesRequiredMembers(service, selected);
    }

    private static BaseTypeDeclarationSyntax MakePublic(BaseTypeDeclarationSyntax declaration)
        => declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword));

    private static SyntaxTokenList WithAccessibility(SyntaxTokenList modifiers, SyntaxKind accessibility)
    {
        var updated = new SyntaxTokenList(modifiers.Where(static token =>
            token.Kind() is not (SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or
                SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword)));
        return updated.Insert(0, SyntaxFactory.Token(accessibility));
    }

    private static TypeDeclarationSyntax AddModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
        => declaration.WithModifiers(AddModifier(declaration.Modifiers, modifier));

    private static SyntaxTokenList AddModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
    {
        if (modifiers.Any(modifier))
            return modifiers;
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (modifiers[index].IsKind(SyntaxKind.PartialKeyword))
                return modifiers.Insert(index, SyntaxFactory.Token(modifier));
        }
        return modifiers.Add(SyntaxFactory.Token(modifier));
    }

    private static TypeDeclarationSyntax RemoveModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
        => declaration.WithModifiers(RemoveModifier(declaration.Modifiers, modifier));

    private static SyntaxTokenList RemoveModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
        => new(modifiers.Where(token => !token.IsKind(modifier)));

    private static bool TryGetEnumUnderlyingTypeSyntax(string type, out TypeSyntax syntax)
    {
        var keyword = type switch
        {
            "System.SByte" or "sbyte" => "sbyte",
            "System.Byte" or "byte" => "byte",
            "System.Int16" or "short" => "short",
            "System.UInt16" or "ushort" => "ushort",
            "System.Int32" or "int" => "int",
            "System.UInt32" or "uint" => "uint",
            "System.Int64" or "long" => "long",
            "System.UInt64" or "ulong" => "ulong",
            _ => string.Empty
        };
        syntax = SyntaxFactory.ParseTypeName(keyword);
        return keyword.Length != 0;
    }

    private static bool TryGetEnumUnderlyingSpecialType(string type, out SpecialType specialType)
    {
        specialType = type switch
        {
            "System.SByte" or "sbyte" => SpecialType.System_SByte,
            "System.Byte" or "byte" => SpecialType.System_Byte,
            "System.Int16" or "short" => SpecialType.System_Int16,
            "System.UInt16" or "ushort" => SpecialType.System_UInt16,
            "System.Int32" or "int" => SpecialType.System_Int32,
            "System.UInt32" or "uint" => SpecialType.System_UInt32,
            "System.Int64" or "long" => SpecialType.System_Int64,
            "System.UInt64" or "ulong" => SpecialType.System_UInt64,
            _ => SpecialType.None
        };
        return specialType != SpecialType.None;
    }

    private static bool TryGetEnumUnderlyingTypeRange(
        string type,
        out decimal minimum,
        out decimal maximum)
    {
        switch (type)
        {
            case "System.SByte":
            case "sbyte":
                minimum = sbyte.MinValue;
                maximum = sbyte.MaxValue;
                return true;
            case "System.Byte":
            case "byte":
                minimum = byte.MinValue;
                maximum = byte.MaxValue;
                return true;
            case "System.Int16":
            case "short":
                minimum = short.MinValue;
                maximum = short.MaxValue;
                return true;
            case "System.UInt16":
            case "ushort":
                minimum = ushort.MinValue;
                maximum = ushort.MaxValue;
                return true;
            case "System.Int32":
            case "int":
                minimum = int.MinValue;
                maximum = int.MaxValue;
                return true;
            case "System.UInt32":
            case "uint":
                minimum = uint.MinValue;
                maximum = uint.MaxValue;
                return true;
            case "System.Int64":
            case "long":
                minimum = long.MinValue;
                maximum = long.MaxValue;
                return true;
            case "System.UInt64":
            case "ulong":
                minimum = ulong.MinValue;
                maximum = ulong.MaxValue;
                return true;
            default:
                minimum = 0;
                maximum = 0;
                return false;
        }
    }

    private sealed class SharpLinkFixAllProvider : FixAllProvider
    {
        internal static SharpLinkFixAllProvider Instance { get; } = new();

        public override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            if (fixAllContext.CodeActionEquivalenceKey?.StartsWith(
                    SignatureKeyPrefix, StringComparison.Ordinal) == true)
            {
                return Task.FromResult<CodeAction?>(null);
            }
            return WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext);
        }
    }
}
