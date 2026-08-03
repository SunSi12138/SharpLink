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
        var diagnostic = context.Diagnostics.First();
        if (diagnostic.Location == Location.None || !diagnostic.Location.IsInSource)
            return;

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
                RegisterDocumentFix(context, diagnostic, "Remove [NonCancellable]", "RemoveNonCancellable",
                    (document, item, ct) => RemoveAttributeAsync(
                        document, item, "SharpLink.Sdk.NonCancellableAttribute", ct));
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
                foreach (var lifetime in new[] { "Singleton", "Connection", "Call" })
                {
                    var value = lifetime;
                    RegisterDocumentFix(context, diagnostic, $"Set RPC service lifetime to {value}",
                        "SetLifetime:" + value,
                        (document, item, ct) => SetServiceLifetimeAsync(document, item, value, ct));
                }
                break;
            case "SHARPLINK028":
                if (diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.PreviousMemberId, out var memberId) &&
                    uint.TryParse(memberId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMemberId) &&
                    parsedMemberId is > 0 and <= 536_870_911)
                {
                    RegisterDocumentFix(context, diagnostic, $"Preserve published member ID {memberId}",
                        "RestoreMemberId:" + memberId,
                        (document, item, ct) => RestoreMemberIdAsync(document, item, memberId!, ct));
                }
                break;
            case "SHARPLINK031":
                if (diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.FixKind, out var requiredKind) &&
                    string.Equals(requiredKind, "RemoveRpcRequired", StringComparison.Ordinal))
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
                    TryGetEnumUnderlyingTypeSyntax(underlyingType!, out _))
                {
                    RegisterDocumentFix(context, diagnostic, $"Restore published enum underlying type {underlyingType}",
                        "RestoreEnumType:" + underlyingType,
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
                RegisterDocumentFix(context, diagnostic, "Use generated default timeout", "UseDefaultTimeout",
                    UseDefaultTimeoutAsync);
                RegisterDocumentFix(context, diagnostic, "Remove [Timeout]", "RemoveTimeout",
                    (document, item, ct) => RemoveAttributeAsync(
                        document, item, "SharpLink.Sdk.TimeoutAttribute", ct));
                break;
            case "SHARPLINK051":
                RegisterDocumentFix(context, diagnostic, "Remove invalid RPC union case mapping",
                    "RemoveInvalidUnionCase", RemoveContainingAttributeAsync);
                break;
            case "SHARPLINK053":
                await RegisterMakeInstanceMethodFixAsync(context, diagnostic).ConfigureAwait(false);
                break;
            case "SHARPLINK055":
                RegisterSolutionFix(context, diagnostic, "Make RPC contract publicly reachable",
                    "MakeContractPublic", MakeContainingTypesPublicAcrossSolutionAsync);
                break;
            case "SHARPLINK056":
                RegisterDocumentFix(context, diagnostic, "Remove [Oneway]", "RemoveOneway",
                    (document, item, ct) => RemoveAttributeAsync(
                        document, item, "SharpLink.Sdk.OnewayAttribute", ct));
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
        if (symbol is null || !await CanSafelyChangeSignatureAsync(
                symbol, context.Document.Project.Solution, allowInvocations: true, context.CancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        RegisterSolutionFix(context, diagnostic, "Add CancellationToken",
            SignatureKeyPrefix + "AddCancellationToken", AddCancellationTokenAsync);
        RegisterDocumentFix(context, diagnostic, "Annotate with [NonCancellable]", "AddNonCancellable",
            AddNonCancellableAsync);
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
                symbol, context.Document.Project.Solution, allowInvocations: true, context.CancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        foreach (var parameter in symbol.Parameters.Where(parameter => IsControlParameter(parameter, kind)))
        {
            var ordinal = parameter.Ordinal;
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
                symbol, context.Document.Project.Solution, allowInvocations: true, context.CancellationToken)
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
                type.BaseType?.SpecialType != SpecialType.System_Object)
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
            RegisterSolutionFix(context, diagnostic, "Make DTO publicly reachable", "MakeDtoAccessible",
                MakeContainingTypesPublicAcrossSolutionAsync);
        }
    }

    private static async Task RegisterMissingContractFixAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<ClassDeclarationSyntax>(
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
        if (candidates.Length != 1 || candidates[0].DeclaringSyntaxReferences.Length == 0)
            return;

        var candidate = candidates[0];
        RegisterSolutionFix(context, diagnostic, $"Annotate {candidate.Name} with [RpcContract]",
            "AnnotateRpcContract",
            (solution, _, _, ct) => AddAttributeToSymbolAsync(
                solution, candidate, "global::SharpLink.Sdk.RpcContract", ct));
    }

    private static async Task RegisterServiceTypeFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<ClassDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type ||
            type.IsGenericType)
        {
            return;
        }

        if (!IsEffectivelyPublic(type) && !type.IsAbstract)
        {
            RegisterSolutionFix(context, diagnostic, "Make RPC service publicly reachable",
                "MakeServicePublic", MakeContainingTypesPublicAcrossSolutionAsync);
        }

        if (type.IsAbstract && IsEffectivelyPublic(type) && IsSafeToMakeConcrete(type))
        {
            RegisterDocumentFix(context, diagnostic, "Make RPC service concrete", "MakeServiceConcrete",
                (document, item, ct) => UpdateTypeAtDiagnosticAsync(
                    document, item, static node => RemoveModifier(node, SyntaxKind.AbstractKeyword), ct));
        }
    }

    private static async Task RegisterConstructorFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<ClassDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null || semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } type)
            return;

        var publicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility == Accessibility.Public)
            .Where(IsSupportedServiceConstructor)
            .ToArray();
        var nonPublicConstructors = type.InstanceConstructors
            .Where(static item => !item.IsImplicitlyDeclared && item.DeclaredAccessibility != Accessibility.Public)
            .Where(IsSupportedServiceConstructor)
            .ToArray();

        if (publicConstructors.Length == 0 && nonPublicConstructors.Length == 1)
        {
            var constructor = nonPublicConstructors[0];
            RegisterSolutionFix(context, diagnostic, $"Make {type.Name} constructor public",
                "MakeConstructorPublic",
                (solution, _, _, ct) => MakeConstructorPublicAsync(solution, constructor, ct));
            return;
        }

        var marker = semanticModel.Compilation.GetTypeByMetadataName(
            "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute");
        var alreadyMarked = publicConstructors.Any(static constructor => constructor.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute",
                StringComparison.Ordinal)));
        if (publicConstructors.Length <= 1 || marker is null || alreadyMarked)
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
        var simpleName = type.Split('.').Last();
        var resolves = semanticModel.Compilation.GetSymbolsWithName(
                simpleName, SymbolFilter.Type, context.CancellationToken)
            .OfType<INamedTypeSymbol>()
            .Any(candidate => string.Equals(
                candidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                expected,
                StringComparison.Ordinal));
        if (!resolves)
            return;

        RegisterDocumentFix(context, diagnostic, $"Restore tag {tag} to {type}",
            "RestoreUnionTag:" + tag + ":" + type,
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
            !CanExposePublicParameterlessConstructor(adapter) ||
            adapter.DeclaringSyntaxReferences.Length == 0)
        {
            return;
        }

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
                symbol, context.Document.Project.Solution, allowInvocations: false, context.CancellationToken)
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

    private static async Task<Document> AddNonCancellableAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var method = await FindNodeAsync<MethodDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        if (method is null)
            return document;
        var updated = method.AddAttributeLists(CreateAttributeList("global::SharpLink.Sdk.NonCancellable"))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return await ReplaceNodeAsync(document, method, updated, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Document> SetServiceLifetimeAsync(
        Document document,
        Diagnostic diagnostic,
        string lifetime,
        CancellationToken cancellationToken)
    {
        var declaration = await FindNodeAsync<ClassDeclarationSyntax>(document, diagnostic, cancellationToken)
            .ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (declaration is null || semanticModel is null)
            return document;

        var attribute = declaration.AttributeLists.SelectMany(static list => list.Attributes)
            .FirstOrDefault(item => AttributeMatches(
                semanticModel, item, "SharpLink.Sdk.RpcServiceAttribute", cancellationToken));
        if (attribute is null)
            return document;

        var value = SyntaxFactory.ParseExpression(
            "global::SharpLink.Sdk.SharpLinkServiceLifetime." + lifetime);
        var argument = SyntaxFactory.AttributeArgument(value)
            .WithNameEquals(SyntaxFactory.NameEquals("Lifetime"));
        var arguments = attribute.ArgumentList?.Arguments ?? default;
        var existing = arguments.FirstOrDefault(static item => item.NameEquals?.Name.Identifier.ValueText == "Lifetime");
        var updatedArguments = existing is null ? arguments.Add(argument) : arguments.Replace(existing, argument);
        var updated = attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(updatedArguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return await ReplaceNodeAsync(document, attribute, updated, cancellationToken).ConfigureAwait(false);
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

    private static async Task<Document> UseDefaultTimeoutAsync(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null)
            return document;
        return await ReplaceNodeAsync(document, attribute,
            attribute.WithArgumentList(null).WithAdditionalAnnotations(Formatter.Annotation), cancellationToken)
            .ConfigureAwait(false);
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
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
            return document;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var attribute = node.AncestorsAndSelf().SelectMany(static item => item.ChildNodes().OfType<AttributeListSyntax>())
            .SelectMany(static list => list.Attributes)
            .FirstOrDefault(item => AttributeMatches(semanticModel, item, metadataName, cancellationToken));
        attribute ??= node.AncestorsAndSelf().OfType<AttributeSyntax>()
            .FirstOrDefault(item => AttributeMatches(semanticModel, item, metadataName, cancellationToken));
        return attribute is null
            ? document
            : await RemoveAttributeNodeAsync(document, attribute, cancellationToken).ConfigureAwait(false);
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

        var referencesByDocument = new Dictionary<DocumentId, List<Microsoft.CodeAnalysis.Text.TextSpan>>();
        for (var current = symbol; current is not null; current = current.ContainingType)
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
        var reference = adapter.DeclaringSyntaxReferences.FirstOrDefault();
        var document = reference is null ? null : solution.GetDocument(reference.SyntaxTree);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var declaration = root?.FindNode(reference!.Span, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (document is null || declaration is null)
            return solution;

        var declarations = declaration.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().ToArray();
        var updatedRoot = root!.ReplaceNodes(declarations, (original, current) =>
        {
            var accessible = MakePublic(current);
            if (original == declaration && accessible is ClassDeclarationSyntax adapterClass)
            {
                var modifiers = adapterClass.Modifiers;
                modifiers = RemoveModifier(modifiers, SyntaxKind.AbstractKeyword);
                if (!modifiers.Any(SyntaxKind.SealedKeyword))
                    modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.SealedKeyword));
                adapterClass = adapterClass.WithModifiers(modifiers);

                var parameterless = adapter.InstanceConstructors.FirstOrDefault(static item =>
                    !item.IsImplicitlyDeclared && item.Parameters.Length == 0);
                if (!adapter.InstanceConstructors.Any(static item =>
                        item.DeclaredAccessibility == Accessibility.Public && item.Parameters.Length == 0))
                {
                    if (parameterless?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)
                        is ConstructorDeclarationSyntax &&
                        adapterClass.Members.OfType<ConstructorDeclarationSyntax>()
                            .FirstOrDefault(static item => item.ParameterList.Parameters.Count == 0)
                        is { } currentConstructor)
                    {
                        adapterClass = adapterClass.ReplaceNode(currentConstructor,
                            currentConstructor.WithModifiers(WithAccessibility(
                                currentConstructor.Modifiers, SyntaxKind.PublicKeyword)));
                    }
                    else
                    {
                        adapterClass = adapterClass.AddMembers(
                            SyntaxFactory.ConstructorDeclaration(adapter.Name)
                                .WithModifiers(SyntaxFactory.TokenList(
                                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                                .WithBody(SyntaxFactory.Block()));
                    }
                }
                return adapterClass.WithAdditionalAnnotations(Formatter.Annotation);
            }
            return accessible.WithAdditionalAnnotations(Formatter.Annotation);
        });
        return solution.WithDocumentSyntaxRoot(document.Id, updatedRoot);
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

    private static bool IsIService(INamedTypeSymbol type)
        => type.Name == "IService" && type.ContainingNamespace.ToDisplayString() == "SharpLink.Sdk";

    private static bool IsCodecAdapter(INamedTypeSymbol type)
        => type.Name == "IRpcCodecAdapter" &&
           type.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions";

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }

    private static bool IsSafeToMakeConcrete(INamedTypeSymbol type)
    {
        if (type.GetMembers().Any(static member => member.IsAbstract))
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
            constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or
                Accessibility.ProtectedOrInternal ||
            constructor.DeclaredAccessibility == Accessibility.Internal &&
            SymbolEqualityComparer.Default.Equals(
                constructor.ContainingAssembly, type.ContainingAssembly));
    }

    private static bool HasOverride(INamedTypeSymbol type, ISymbol abstractMember)
    {
        foreach (var candidate in type.GetMembers(abstractMember.Name))
        {
            for (var current = candidate; current is not null; current = GetOverriddenMember(current))
            {
                if (SymbolEqualityComparer.Default.Equals(current, abstractMember))
                    return true;
            }
        }
        return false;
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
        return selected is not null && IsSupportedServiceConstructor(selected);
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
        => modifiers.Any(modifier) ? modifiers : modifiers.Add(SyntaxFactory.Token(modifier));

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
