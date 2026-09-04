namespace SharpLink.Generator;

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
}
