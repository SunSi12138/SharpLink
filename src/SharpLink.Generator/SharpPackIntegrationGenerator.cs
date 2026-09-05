namespace SharpLink.Generator;

/// <summary>
/// Generates SharpPack sidecar formatters for payloads whose finalized SharpLink Codec binding
/// selects the SharpPack Adapter.
/// </summary>
[Generator]
public sealed class SharpPackIntegrationGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedSharpPackPayloadRule = new(
        id: "SLSP0001",
        title: "SharpPack-routed RPC payload is not build-time serializable",
        messageFormat: "SharpPack-routed type '{0}' is unsupported: {1}",
        category: "SharpLink.Serializer.SharpPack.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every closed RPC payload routed to SharpPack must either have authoritative SharpPack support or a generated sidecar formatter.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analysis = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
            RpcGenerator.RunSharpPackIntegrationAnalysis(compilation, cancellationToken));

        context.RegisterSourceOutput(analysis, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedSharpPackPayloadRule,
                    diagnostic.Location,
                    diagnostic.TypeName,
                    diagnostic.Detail));
            }

            if (!result.HasBindings)
                return;

            spc.AddSource(
                "SharpLink.SharpPackIntegration.g.cs",
                SourceText.From(RpcGenerator.EmitSharpPackIntegration(result), Encoding.UTF8));
        });
    }
}

public partial class RpcGenerator
{
    internal static SharpPackIntegrationAnalysisResult RunSharpPackIntegrationAnalysis(
        Compilation compilation,
        CancellationToken cancellationToken)
        => AnalyzeSharpPackIntegration(compilation, cancellationToken);

    internal static string EmitSharpPackIntegration(SharpPackIntegrationAnalysisResult analysis)
        => GenerateSharpPackIntegration(analysis);
}
