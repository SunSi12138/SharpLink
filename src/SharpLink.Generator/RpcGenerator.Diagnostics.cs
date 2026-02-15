namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static readonly DiagnosticDescriptor InvalidReturnTypeRule = new(
        id: "SHARPLINK001",
        title: "Invalid RPC Return Type",
        messageFormat: "RPC method '{0}' must return Task/Task<T>/ValueTask/ValueTask<T>/IAsyncEnumerable<T>, but returns '{1}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MultipleCancellationTokensRule = new(
        id: "SHARPLINK002",
        title: "Invalid RPC CancellationToken Signature",
        messageFormat: "RPC method '{0}' can declare at most one CancellationToken parameter",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor StreamParameterCountRule = new(
        id: "SHARPLINK003",
        title: "Invalid RPC Stream Parameter Count",
        messageFormat: "RPC method '{0}' defines {1} stream parameters, but at most 127 are supported",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TimeoutRequiresCancellationTokenRule = new(
        id: "SHARPLINK004",
        title: "Timeout Attribute Requires CancellationToken",
        messageFormat: "RPC method '{0}' uses [Timeout] but does not declare a CancellationToken parameter",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
