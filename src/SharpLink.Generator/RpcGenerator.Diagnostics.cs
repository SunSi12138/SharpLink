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

    private static readonly DiagnosticDescriptor MissingCancellationTokenRule = new(
        id: "SHARPLINK004",
        title: "RPC Method Cannot Cooperatively Stop Application Work",
        messageFormat: "RPC method '{0}' has no CancellationToken; add one or explicitly annotate the method with [NonCancellable]",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Client deadlines still fail locally, but server application work may continue after the call is abandoned.");

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

    private static readonly DiagnosticDescriptor UnsupportedGeneratedDtoRule = new(
        id: "SHARPLINK009",
        title: "DTO Type Is Not Supported by the Native Codec Generator",
        messageFormat: "DTO type '{0}' cannot use the native generated Codec: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CyclicDtoGraphRule = new(
        id: "SHARPLINK010",
        title: "Cyclic DTO Graph Is Not Supported",
        messageFormat: "DTO type '{0}' participates in a cyclic generated Codec graph: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateDtoMemberIdRule = new(
        id: "SHARPLINK011",
        title: "DTO Member IDs Must Be Unique",
        messageFormat: "DTO type '{0}' has duplicate wire member ID: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DtoConstructionRule = new(
        id: "SHARPLINK012",
        title: "DTO Cannot Be Constructed by Generated Code",
        messageFormat: "DTO type '{0}' has no supported constructor/member assignment plan: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DtoDepthRule = new(
        id: "SHARPLINK013",
        title: "DTO Graph Is Too Deep",
        messageFormat: "DTO type '{0}' exceeds the generated Codec depth limit: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor StreamingMissingCancellationTokenRule = new(
        id: "SHARPLINK014",
        title: "Streaming RPC Must Declare Its Cancellation Contract",
        messageFormat: "Streaming RPC method '{0}' has no CancellationToken; add one or explicitly annotate the method with [NonCancellable]",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Streaming calls retain connection, flow-control, and dispatcher resources until their framework pump is terminated.");

    private static readonly DiagnosticDescriptor ConflictingCancellationContractRule = new(
        id: "SHARPLINK015",
        title: "RPC Cancellation Contract Is Contradictory",
        messageFormat: "RPC method '{0}' cannot declare both [NonCancellable] and CancellationToken",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
