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
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method)
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
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType ||
                type.BaseType?.SpecialType != SpecialType.System_Object ||
                HasMembersIncompatibleWithSealing(type))
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
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } service ||
            !HasValidServiceShapeAfterContractAnnotation(service))
            return;

        var candidates = service.AllInterfaces
            .Where(static item => item.Locations.Any(static location => location.IsInSource))
            .Where(static item => item.AllInterfaces.Any(IsIService))
            .Where(static item => !HasAttribute(item, "SharpLink.Sdk.RpcContractAttribute"))
            .Where(item => HasRegularEditableDeclaration(
                item, context.Document.Project.Solution))
            .Where(HasValidRpcContractShapeForAnnotation)
            .Where(static item => !item.IsGenericType &&
                                  !GetContainingTypes(item).Any(static containing => containing.IsGenericType))
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

        var canMakePublic = TryGetPublicizationClosure(
            type, context.Document.Project.Solution, out _);
        var hasValidLifetime = HasValidServiceLifetime(type);
        if (!IsEffectivelyPublic(type) && !type.IsAbstract && canMakePublic && hasValidLifetime &&
            HasValidServiceActivationShape(type))
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service publicly reachable",
                "MakeServicePublic", MakeContainingTypesPublicAcrossSolutionAsync);
        }

        if (type.IsAbstract && IsEffectivelyPublic(type) && IsSafeToMakeConcrete(type) && hasValidLifetime &&
            HasValidServiceActivationShapeAfterMakingConcrete(type))
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service concrete", "MakeServiceConcrete",
                (solution, _, _, ct) => FixServiceShapeAcrossSolutionAsync(
                    solution, type, makePublic: false, ct));
        }
        else if (type.IsAbstract && !IsEffectivelyPublic(type) && IsSafeToMakeConcrete(type) && canMakePublic &&
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
            marker is null || hasValidSelectedConstructor ||
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
        var attributeReference = service?.GetAttributes()
            .FirstOrDefault(static attribute => string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "SharpLink.Sdk.RpcServiceAttribute",
                StringComparison.Ordinal))
            ?.ApplicationSyntaxReference;
        if (attributeReference is null ||
            !IsRegularEditableDocument(context.Document.Project.Solution, attributeReference.SyntaxTree))
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
            IsObsoleteWithError(namedCase) ||
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
            .Where(item => HasRegularEditableDeclaration(
                item, context.Document.Project.Solution))
            .Where(item => HasDeclarationInProject(
                item, context.Document.Project.Solution, context.Document.Project.Id))
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
            HasMembersIncompatibleWithSealing(adapter) ||
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
            contract.Arity != 0 ||
            GetContainingTypes(contract).Any(static containing => containing.Arity != 0) ||
            !HasValidRpcContractShapeForAnnotation(contract))
        {
            return;
        }

        RegisterDocumentFix(context, diagnostic, "Add IService to RPC contract", "AddIService",
            AddIServiceAsync);
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
}
