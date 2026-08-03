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
                await RegisterAddIServiceFixAsync(context, diagnostic).ConfigureAwait(false);
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
                    RegisterSolutionFix(context, diagnostic, $"Preserve published member ID {memberId}",
                        "RestoreMemberId",
                        (solution, documentId, item, ct) => RestoreMemberIdAsync(
                            solution, documentId, item, memberId!, ct));
                }
                break;
            case "SHARPLINK031":
                if (diagnostic.Properties.TryGetValue(SharpLinkDiagnosticProperties.FixKind, out var requiredKind) &&
                    string.Equals(requiredKind, "RemoveRpcRequired", StringComparison.Ordinal))
                {
                    await RegisterRemoveRpcRequiredFixAsync(context, diagnostic).ConfigureAwait(false);
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
            IsObsoleteWithError(type) ||
            !TryGetPublicizationClosure(type, context.Document.Project.Solution, out _))
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

    private sealed class SharpLinkFixAllProvider : FixAllProvider
    {
        internal static SharpLinkFixAllProvider Instance { get; } = new();

        public override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            if (fixAllContext.CodeActionEquivalenceKey?.StartsWith(
                    SignatureKeyPrefix, StringComparison.Ordinal) == true ||
                fixAllContext.CodeActionEquivalenceKey is
                    "RemoveInvalidUnionCase" or "RemoveBuiltinAdapterBinding")
            {
                return Task.FromResult<CodeAction?>(null);
            }
            return WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext);
        }
    }
}
