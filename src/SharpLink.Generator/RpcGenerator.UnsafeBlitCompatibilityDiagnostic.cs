namespace SharpLink.Generator;

/// <summary>
/// Reports non-blocking guidance for RPC payloads whose resolved final Codec graph contains
/// implicit UnsafeBlit over source-defined AutoLayout value types.
/// </summary>
[Generator]
public sealed class UnsafeBlitCompatibilityDiagnosticGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var diagnostics = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
            RpcGenerator.AnalyzeUnsafeBlitAutoLayoutDiagnostics(compilation, cancellationToken));

        context.RegisterSourceOutput(diagnostics, static (productionContext, items) =>
        {
            foreach (var diagnostic in items)
                productionContext.ReportDiagnostic(diagnostic);
        });
    }
}

public partial class RpcGenerator
{
    private static readonly DiagnosticDescriptor ImplicitUnsafeBlitAutoLayoutRule = new(
        id: "SHARPLINK064",
        title: "Implicit UnsafeBlit Contains Source-Defined AutoLayout",
        messageFormat: "RPC payload '{0}' resolves through implicit UnsafeBlit, and the resolved physical graph contains source-defined AutoLayout type '{1}' at '{2}'. Raw-memory wire layout can vary across runtimes; for stable cross-runtime raw wire prefer LayoutKind.Sequential or LayoutKind.Explicit, or bind an explicit custom/adapter codec.",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Source-defined AutoLayout inside a resolved implicit UnsafeBlit plan can make raw-memory wire layout runtime-dependent. This diagnostic is advisory and does not change Codec selection or generated wire behavior.");

    internal static ImmutableArray<Diagnostic> AnalyzeUnsafeBlitAutoLayoutDiagnostics(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var state = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        _ = state.AnalyzeWithFinalCodecBindings();

        return state.BuildUnsafeBlitAutoLayoutDiagnostics()
            .Select(static item => Diagnostic.Create(
                ImplicitUnsafeBlitAutoLayoutRule,
                item.Location,
                item.PayloadType,
                item.TypeName,
                item.FieldPath))
            .ToImmutableArray();
    }
}
