namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static readonly DiagnosticDescriptor UnsupportedSharpPackPayloadRule = new(
        id: "SLSP0001",
        title: "SharpPack-routed RPC payload is not build-time serializable",
        messageFormat: "SharpPack-routed type '{0}' is unsupported: {1}",
        category: "SharpLink.Serializer.SharpPack.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every closed RPC payload routed to SharpPack must either have authoritative SharpPack support or a generated sidecar formatter.");
}
