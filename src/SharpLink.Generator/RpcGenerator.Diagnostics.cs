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

    private static readonly DiagnosticDescriptor GenericUsageInRpcRule = new(
        id: "SHARPLINK005",
        title: "Generic Type Parameter Not Supported in RPC Contract",
        messageFormat: "RPC contract '{0}' contains unsupported generic type parameter usage in '{1}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RpcContractMustInheritIServiceRule = new(
        id: "SHARPLINK006",
        title: "RPC Contract Must Inherit IService",
        messageFormat: "RPC contract interface '{0}' must inherit SharpLink.Sdk.IService",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MultipleCallOptionsRule = new(
        id: "SHARPLINK007",
        title: "Invalid RPC SharpLinkCallOptions Signature",
        messageFormat: "RPC method '{0}' can declare at most one SharpLinkCallOptions parameter",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ControlParameterOrderRule = new(
        id: "SHARPLINK008",
        title: "Invalid RPC Control Parameter Order",
        messageFormat: "RPC method '{0}' must place SharpLinkCallOptions and CancellationToken last, with CancellationToken last when both are present",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
