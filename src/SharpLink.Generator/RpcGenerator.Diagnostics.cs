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

    private static readonly DiagnosticDescriptor RpcServiceMissingContractRule = new(
        id: "SHARPLINK016",
        title: "RPC Service Must Implement an RPC Contract",
        messageFormat: "RPC service '{0}' is invalid: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RpcServiceMultipleContractsRule = new(
        id: "SHARPLINK017",
        title: "RPC Service Must Own Exactly One Contract",
        messageFormat: "RPC service '{0}' is invalid: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RpcServiceTypeRule = new(
        id: "SHARPLINK018",
        title: "RPC Service Type Cannot Be Activated",
        messageFormat: "RPC service '{0}' is invalid: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RpcServiceConstructorRule = new(
        id: "SHARPLINK019",
        title: "RPC Service Constructor Is Ambiguous",
        messageFormat: "RPC service '{0}' is invalid: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RpcServiceLifetimeRule = new(
        id: "SHARPLINK020",
        title: "RPC Service Lifetime Is Invalid",
        messageFormat: "RPC service '{0}' is invalid: {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor StaticContractConflictRule = new(
        id: "SHARPLINK021",
        title: "Static RPC Contract Route Conflict",
        messageFormat: "Static contract '{0}' ({1}) has conflicting fingerprints '{2}' and '{3}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor StaticMethodConflictRule = new(
        id: "SHARPLINK022",
        title: "Static RPC Method Route Conflict",
        messageFormat: "Static method '{0}' ({1}) has conflicting fingerprints '{2}' and '{3}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor StaticServiceConflictRule = new(
        id: "SHARPLINK023",
        title: "Static RPC Service Ownership Conflict",
        messageFormat: "Static service contract '{0}' ({1}) has conflicting owners '{2}' and '{3}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ContractBaselineInvalidRule = CompatibilityRule(
        "SHARPLINK024", "Contract Baseline Is Invalid");

    private static readonly DiagnosticDescriptor ContractBaselineVersionRule = CompatibilityRule(
        "SHARPLINK025", "Contract Baseline Version Is Unsupported");

    private static readonly DiagnosticDescriptor ContractIdCompatibilityRule = CompatibilityRule(
        "SHARPLINK026", "Contract ID Is Incompatible");

    private static readonly DiagnosticDescriptor MethodIdCompatibilityRule = CompatibilityRule(
        "SHARPLINK027", "Method ID Is Incompatible");

    private static readonly DiagnosticDescriptor MemberIdCompatibilityRule = CompatibilityRule(
        "SHARPLINK028", "DTO Member ID Is Incompatible");

    private static readonly DiagnosticDescriptor CallShapeCompatibilityRule = CompatibilityRule(
        "SHARPLINK029", "RPC Call Shape Is Incompatible");

    private static readonly DiagnosticDescriptor WireTypeCompatibilityRule = CompatibilityRule(
        "SHARPLINK030", "RPC Wire Type Is Incompatible");

    private static readonly DiagnosticDescriptor RequiredMemberCompatibilityRule = CompatibilityRule(
        "SHARPLINK031", "Required DTO Member Change Is Incompatible");

    private static readonly DiagnosticDescriptor EnumCompatibilityRule = CompatibilityRule(
        "SHARPLINK032", "Enum Underlying Type Is Incompatible");

    private static readonly DiagnosticDescriptor UnionTagCompatibilityRule = CompatibilityRule(
        "SHARPLINK033", "Union Tag Was Reused");

    private static readonly DiagnosticDescriptor MethodRemovedCompatibilityRule = CompatibilityRule(
        "SHARPLINK034", "Existing RPC Method Was Removed");

    private static readonly DiagnosticDescriptor ContractRemovedCompatibilityRule = CompatibilityRule(
        "SHARPLINK035", "Existing RPC Contract Was Removed");

    private static readonly DiagnosticDescriptor ContractManifestOutputRule = new(
        id: "SHARPLINK036",
        title: "Contract Manifest Could Not Be Written",
        messageFormat: "Could not write SharpLink contract Manifest '{0}': {1}",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ServiceRouteRemovedCompatibilityRule = CompatibilityRule(
        "SHARPLINK037", "Existing Service Route Was Removed");

    private static readonly DiagnosticDescriptor InvalidClusterKeyRule = new(
        id: "SHARPLINK038",
        title: "Multi-Cluster Key Is Invalid",
        messageFormat: "Cluster key '{0}' must contain 1 to 64 ASCII characters, start with a letter or digit, and then contain only letters, digits, '.', '_', or '-'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingClusterRouteRule = new(
        id: "SHARPLINK039",
        title: "Contract Assembly Has Conflicting Cluster Routes",
        messageFormat: "Contract assembly '{0}' is routed to both cluster '{1}' and cluster '{2}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingClusterRouteManifestRule = new(
        id: "SHARPLINK040",
        title: "Cluster Route Marker Lacks Generated Manifest",
        messageFormat: "Cluster route marker assembly '{0}' does not expose a compatible generated SharpLink manifest",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidClusterRouteAttributeRule = new(
        id: "SHARPLINK041",
        title: "Multi-Cluster Route Attribute Is Invalid",
        messageFormat: "SharpLinkClusterContractAssembly requires a literal cluster key and a concrete marker type",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidAdapterRegistrationRule = AdapterRule(
        "SHARPLINK042", "Codec Adapter Registration Is Invalid");

    private static readonly DiagnosticDescriptor InvalidAdapterTypeRule = AdapterRule(
        "SHARPLINK043", "Codec Adapter Type Is Invalid");

    private static readonly DiagnosticDescriptor SelectorAdapterConflictRule = AdapterRule(
        "SHARPLINK044", "Selector Attribute Has Multiple Codec Adapters");

    private static readonly DiagnosticDescriptor AdapterSelectionConflictRule = AdapterRule(
        "SHARPLINK045", "RPC Payload Selects Multiple Codec Adapters");

    private static readonly DiagnosticDescriptor InvalidAdapterBindingRule = AdapterRule(
        "SHARPLINK046", "RpcCodecAdapter Usage Is Invalid");

    private static readonly DiagnosticDescriptor InvalidAdapterTargetRule = AdapterRule(
        "SHARPLINK047", "Codec Adapter Target Is Invalid");

    private static readonly DiagnosticDescriptor AdapterIdentityConflictRule = AdapterRule(
        "SHARPLINK048", "Codec Adapter Identity Conflicts");

    private static readonly DiagnosticDescriptor BuiltinAdapterOverrideRule = AdapterRule(
        "SHARPLINK049", "Built-in Codec Cannot Be Rebound");

    private static DiagnosticDescriptor AdapterRule(string id, string title)
        => new(
            id: id,
            title: title,
            messageFormat: "Codec Adapter error for '{0}': {1}",
            category: "SharpLink.Generator",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

    private static DiagnosticDescriptor CompatibilityRule(string id, string title)
        => new(
            id: id,
            title: title,
            messageFormat: "Contract compatibility error for '{0}': {1}. Suggested fix: {2}.",
            category: "SharpLink.Compatibility",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
}
