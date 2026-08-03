namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
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
        if (symbol is null || IsObsoleteWithError(symbol))
            return;

        var canAddCancellationToken = await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true,
                allowSignatureQualifiedCrefs: false, context.CancellationToken)
            .ConfigureAwait(false);
        if (canAddCancellationToken)
        {
            var relatedMethods = await FindRelatedMethodsAsync(
                symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
            canAddCancellationToken = !relatedMethods.Any(HasNonCancellableAttribute) &&
                                      !relatedMethods.Any(static related =>
                                          related.Parameters.Any(static parameter =>
                                              parameter.IsOptional || parameter.IsParams)) &&
                                      CanApplySignatureEditWithoutCollisions(
                                          relatedMethods,
                                          new SignatureEditPlan(SignatureEditKind.AddCancellationToken)) &&
                                      CanReorderControlParametersWithoutBreakingHandlerDependencies(relatedMethods) &&
                                      await CanIntroduceNamedArgumentsAtInvocationSitesAsync(
                                              relatedMethods,
                                              context.Document.Project.Solution,
                                              context.CancellationToken)
                                          .ConfigureAwait(false);
        }

        if (canAddCancellationToken)
        {
            RegisterSolutionFix(context, diagnostic, "Add CancellationToken",
                SignatureKeyPrefix + "AddCancellationToken", AddCancellationTokenAsync);
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            symbol, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        if (equivalentMethods.Length != 0 &&
            equivalentMethods.All(candidate => HasOnlyRegularEditableDeclarations(
                candidate, context.Document.Project.Solution)))
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
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method ||
            IsObsoleteWithError(method))
        {
            return;
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var attributedMethods = equivalentMethods.Where(HasNonCancellableAttribute).ToImmutableArray();
        var attributes = attributedMethods.SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsNonCancellableAttribute).ToImmutableArray();
        if (attributes.Length == 0 || attributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
        {
            return;
        }

        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [NonCancellable]",
            "RemoveNonCancellable",
            (solution, _, _, ct) => RemoveAttributesAsync(
                solution,
                attributes.Select(static attribute => attribute.ApplicationSyntaxReference!).ToImmutableArray(),
                ct));
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
        if (method is null || semanticModel?.GetDeclaredSymbol(method, context.CancellationToken) is not { } symbol ||
            IsObsoleteWithError(symbol))
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
                !CanRemoveControlParametersWithoutBreakingNameReferences(relatedMethods, kind, ordinal) ||
                !await CanRemoveControlArgumentsWithoutSideEffectsAsync(
                        relatedMethods,
                        kind,
                        ordinal,
                        context.Document.Project.Solution,
                        context.CancellationToken)
                    .ConfigureAwait(false))
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
            IsObsoleteWithError(symbol) ||
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
            !CanReorderControlParametersWithoutBreakingHandlerDependencies(relatedMethods) ||
            !await CanIntroduceNamedArgumentsAtInvocationSitesAsync(
                    relatedMethods,
                    context.Document.Project.Solution,
                    context.CancellationToken)
                .ConfigureAwait(false))
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
            var validationTypes = await GetDtoTypesToValidateAfterSealingAsync(
                type, context.Document.Project, context.CancellationToken).ConfigureAwait(false);
            if (type.TypeKind != TypeKind.Class || type.IsAbstract ||
                IsObsoleteWithError(type) ||
                type.BaseType?.SpecialType != SpecialType.System_Object ||
                HasMembersIncompatibleWithSealing(type) ||
                !HasOnlyRegularEditableDeclarations(type, context.Document.Project.Solution) ||
                validationTypes.IsDefaultOrEmpty ||
                validationTypes.Any(candidate =>
                    !SharpLink.Generator.RpcGenerator.CanGenerateDtoAfterSealing(
                        semanticModel.Compilation, candidate, context.CancellationToken)))
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
                "MakeDtoAccessible",
                SharpLink.Generator.RpcGenerator.CanGenerateDtoAfterPublicization).ConfigureAwait(false);
        }
    }

    private static async Task RegisterMissingContractFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<TypeDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } service ||
            !HasValidServiceShapeAfterContractAnnotation(service))
            return;

        var candidates = service.AllInterfaces
            .Where(static item => item.Locations.Any(static location => location.IsInSource))
            .Where(static item => item.AllInterfaces.Any(IsIService))
            .Where(static item => !HasAttribute(item, "SharpLink.Sdk.RpcContractAttribute"))
            .Where(item => HasOnlyRegularEditableDeclarations(
                item, context.Document.Project.Solution))
            .Where(HasValidRpcContractShapeForAnnotation)
            .Where(item => SharpLink.Generator.RpcGenerator.CanGenerateContractPayloadCodecs(
                semanticModel.Compilation, item, context.CancellationToken))
            .Where(static item => !item.IsGenericType &&
                                  !GetContainingTypes(item).Any(static containing => containing.IsGenericType))
            .ToArray();
        if (candidates.Length != 1 || candidates[0].DeclaringSyntaxReferences.Length == 0 ||
            !IsEffectivelyPublic(candidates[0]))
            return;

        var candidate = candidates[0];
        if (await CountRpcServiceImplementationsAsync(
                candidate,
                service,
                context.Document.Project.Solution,
                context.CancellationToken).ConfigureAwait(false) != 0)
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
            type.TypeKind != TypeKind.Class || type.IsGenericType || IsObsoleteWithError(type))
        {
            return;
        }

        var canMakePublic = TryGetPublicizationClosure(
            type, context.Document.Project.Solution, out _);
        var hasOnlyRegularDeclarations = HasOnlyRegularEditableDeclarations(
            type, context.Document.Project.Solution);
        var hasValidLifetime = HasValidServiceLifetime(type);
        if (!IsEffectivelyPublic(type) && !type.IsAbstract && canMakePublic && hasValidLifetime &&
            HasValidServiceActivationShape(type))
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service publicly reachable",
                "MakeServicePublic", MakeContainingTypesPublicAcrossSolutionAsync);
        }

        if (type.IsAbstract && IsEffectivelyPublic(type) && hasOnlyRegularDeclarations &&
            IsSafeToMakeConcrete(type) && hasValidLifetime &&
            HasValidServiceActivationShapeAfterMakingConcrete(type))
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service concrete", "MakeServiceConcrete",
                (solution, _, _, ct) => FixServiceShapeAcrossSolutionAsync(
                    solution, type, makePublic: false, ct));
        }
        else if (type.IsAbstract && !IsEffectivelyPublic(type) && hasOnlyRegularDeclarations &&
                 IsSafeToMakeConcrete(type) && canMakePublic &&
                 hasValidLifetime &&
                 HasValidServiceActivationShapeAfterMakingConcrete(type))
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
            type.TypeKind != TypeKind.Class || IsObsoleteWithError(type))
            return;

        var allPublicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var publicConstructors = allPublicConstructors
            .Where(IsSupportedServiceConstructor)
            .Where(constructor => ConstructorSatisfiesRequiredMembers(type, constructor))
            .Where(constructor => CanApplyConstructorSelectionAttribute(
                constructor, context.CancellationToken))
            .Where(constructor => HasRegularEditableDeclaration(
                constructor, context.Document.Project.Solution))
            .ToArray();
        var nonPublicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility != Accessibility.Public)
            .Where(IsSupportedServiceConstructor)
            .Where(constructor => ConstructorSatisfiesRequiredMembers(type, constructor))
            .ToArray();

        if (allPublicConstructors.Length == 0 && nonPublicConstructors.Length == 1 &&
            CanExposeAsPublic(nonPublicConstructors[0]) &&
            HasExplicitConstructorDeclaration(nonPublicConstructors[0], context.CancellationToken) &&
            HasRegularEditableDeclaration(nonPublicConstructors[0], context.Document.Project.Solution))
        {
            var constructor = nonPublicConstructors[0];
            RegisterSolutionFix(context, diagnostic, $"Make {type.Name} constructor public",
                "MakeConstructorPublic",
                (solution, _, _, ct) => MakeConstructorPublicAsync(solution, constructor, ct));
            return;
        }

        var marker = semanticModel.Compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute");
        var markedConstructors = allPublicConstructors.Where(static constructor =>
            constructor.GetAttributes().Any(IsActivatorUtilitiesConstructorAttribute)).ToArray();
        var markerReferences = markedConstructors
            .SelectMany(static constructor => constructor.GetAttributes())
            .Where(IsActivatorUtilitiesConstructorAttribute)
            .Select(static attribute => attribute.ApplicationSyntaxReference)
            .ToArray();
        var hasValidSelectedConstructor = markedConstructors.Length == 1 &&
            publicConstructors.Any(constructor =>
                SymbolEqualityComparer.Default.Equals(constructor, markedConstructors[0]));
        if (allPublicConstructors.Length <= 1 || publicConstructors.Length == 0 ||
            marker is null ||
            !CanApplyConstructorSelectionMarker(
                marker, semanticModel, diagnostic.Location.SourceSpan.Start) ||
            hasValidSelectedConstructor ||
            markerReferences.Any(reference => reference is null ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
            return;

        foreach (var constructor in publicConstructors)
        {
            var selected = constructor;
            var signature = string.Join(", ", constructor.Parameters.Select(static item =>
                item.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            RegisterSolutionFix(context, diagnostic, $"Select constructor {type.Name}({signature})",
                "SelectConstructor:" + constructor.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                (solution, _, _, ct) => SelectServiceConstructorAsync(
                    solution,
                    markerReferences.Select(static reference => reference!).ToImmutableArray(),
                    selected,
                    ct));
        }
    }

    private static async Task RegisterRemoveRpcRequiredFixAsync(
        CodeFixContext context,
        Diagnostic diagnostic)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var node = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node is null || semanticModel is null ||
            !TryGetRpcMemberTarget(node, semanticModel, context.CancellationToken, out var target, out _) ||
            target is null)
        {
            return;
        }

        var attributes = GetAttributesAcrossPartialPropertyParts(
            target, "SharpLink.Sdk.RpcRequiredAttribute");
        if (attributes.Length != 1 ||
            attributes[0].ApplicationSyntaxReference is not { } reference ||
            !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, "Remove [RpcRequired]", "RemoveRpcRequired",
            (solution, _, _, ct) => RemoveAttributeAtReferenceAsync(solution, reference, ct));
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
        if (service?.TypeKind != TypeKind.Class || IsObsoleteWithError(service) ||
            !HasValidServiceActivationShape(service))
            return;
        var serviceAttribute = service.GetAttributes().FirstOrDefault(IsRpcServiceAttribute);
        var attributeReference = serviceAttribute?.ApplicationSyntaxReference;
        var lifetimeTypeName = serviceAttribute?.AttributeClass?.GetMembers("Lifetime")
            .OfType<IPropertySymbol>()
            .Select(static property => property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .SingleOrDefault();
        if (attributeReference is null ||
            string.IsNullOrWhiteSpace(lifetimeTypeName) ||
            !IsRegularEditableDocument(context.Document.Project.Solution, attributeReference.SyntaxTree))
            return;

        foreach (var lifetime in new[] { "Singleton", "Connection", "Call" })
        {
            var value = lifetime;
            RegisterSolutionFix(context, diagnostic, $"Set RPC service lifetime to {value}",
                "SetLifetime:" + value,
                (solution, _, _, ct) => SetServiceLifetimeAsync(
                    solution, attributeReference, lifetimeTypeName!, value, ct));
        }
    }

}
