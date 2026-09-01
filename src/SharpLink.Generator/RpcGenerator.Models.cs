namespace SharpLink.Generator;

internal record RpcServiceModel(
    string ServiceName,
    string ServiceNamespace,
    string ServiceFullName,
    RpcInterfaceModel Interface,
    string Lifetime,
    EquatableArray<RpcConstructorParameterModel> ConstructorParameters,
    EquatableArray<string> AssemblyDependencies,
    Location? Location);

internal record RpcConstructorParameterModel(string Name, string TypeName);

internal record RpcInterfaceModel(
    string Name,
    string Namespace,
    string FullName,
    long Hash,
    EquatableArray<RpcMethodModel> Methods,
    string Fingerprint,
    EquatableArray<string> AssemblyDependencies,
    Location? Location);

internal record RpcMethodModel(
    string Name,
    string ReturnType,
    string DisplayReturnType,
    bool IsGenericTask,
    bool IsStreamReturn,
    string? StreamItemType,
    string? DisplayStreamItemType,
    string? GenericArgumentType,
    string? DisplayGenericArgumentType,
    bool IsVoid,
    bool IsOneWay,
    bool HasCancellationToken,
    bool HasTimeoutAttribute,
    long? TimeoutTicks,
    bool IsIdempotent,
    long Hash,
    EquatableArray<RpcParameterModel> Parameters,
    string RequestSchema,
    string ResponseSchema,
    string Fingerprint,
    bool ResponseNullable,
    string? ResponseEnumUnderlyingType,
    string? StreamItemEnumUnderlyingType,
    Location? Location)
{
    internal bool ReturnsValueTask => ReturnType.StartsWith(
        "global::System.Threading.Tasks.ValueTask",
        StringComparison.Ordinal);
}

internal record RpcParameterModel(
    string Name,
    string Type,
    string DisplayType,
    bool IsStream,
    string? StreamItemType,
    string? DisplayStreamItemType,
    bool IsBlittable,
    bool IsValueType,
    bool IsNullableReference,
    bool PayloadNullable,
    bool IsCancellationToken,
    string? EnumUnderlyingType,
    string? StreamItemEnumUnderlyingType,
    Location? Location);

internal readonly record struct InvalidRpcMethodModel(
    InvalidRpcMethodKind Kind,
    string MethodName,
    string Detail,
    Location? Location);
internal enum InvalidRpcMethodKind
{
    ReturnType,
    Timeout,
    ByReference,
    Static,
    ContractMember,
    OnewayReturn,
    InheritedSignatureConflict
}
internal readonly record struct InvalidCancellationTokenMethodModel(string MethodName, Location? Location);
internal readonly record struct InvalidControlParameterOrderModel(string MethodName, Location? Location);
internal readonly record struct InvalidStreamCountMethodModel(string MethodName, int StreamParameterCount, Location? Location);
internal readonly record struct NonCancellableRpcMethodModel(string MethodName, Location? Location);
internal readonly record struct StreamingWithoutCancellationModel(string MethodName, Location? Location);
internal readonly record struct ConflictingCancellationContractModel(string MethodName, Location? Location);
internal readonly record struct InvalidGenericUsageModel(string SymbolName, string TypeName, Location? Location);
internal readonly record struct RpcContractDiagnosticModel(
    RpcContractDiagnosticKind Kind,
    string InterfaceName,
    Location? Location);
internal enum RpcContractDiagnosticKind
{
    Inheritance,
    Accessibility
}
internal readonly record struct RpcServiceDiagnosticModel(
    RpcServiceDiagnosticKind Kind,
    string ServiceName,
    string Detail,
    Location? Location);

internal enum RpcServiceDiagnosticKind
{
    MissingContract,
    MultipleContracts,
    InvalidType,
    InvalidConstructor,
    InvalidLifetime
}

internal readonly record struct StaticRouteConflictModel(
    StaticRouteConflictKind Kind,
    string Name,
    long Id,
    string ExistingFingerprint,
    string IncomingFingerprint,
    Location? Location);

internal readonly record struct ReferencedManifestBootstrapModel(
    string AssemblyIdentity,
    string ManifestTypeName,
    bool HasRegisterMethod);

internal sealed record RpcUnionModel(
    string TypeName,
    EquatableArray<RpcUnionCaseModel> Cases,
    Location? Location);

internal sealed record RpcUnionCaseModel(
    int Tag,
    string TypeName,
    string? InvalidDetail,
    Location? Location);

internal enum StaticRouteConflictKind
{
    Contract,
    Method,
    Service
}

internal enum GeneratedCodecKind
{
    Adapter,
    Dto,
    Array,
    List,
    Dictionary,
    Memory,
    ReadOnlyMemory,
    ImmutableArray,
    Nullable,
    Custom
}

internal enum GeneratedMemberKind
{
    Fixed,
    NullableFixed,
    String,
    Complex
}

internal sealed record GeneratedMemberModel(
    string Name,
    string Identifier,
    string TypeName,
    uint FieldId,
    GeneratedMemberKind Kind,
    string? FixedTypeName,
    int FixedSize,
    bool Required,
    bool Nullable,
    bool NonNullableReference,
    bool ConstructorBound,
    bool InitializerBound,
    bool HasExplicitId,
    string? EnumUnderlyingType,
    Location? Location);

internal sealed record GeneratedCodecModel(
    string TypeName,
    string CodecName,
    string SchemaId,
    GeneratedCodecKind Kind,
    bool IsReferenceType,
    ImmutableArray<GeneratedMemberModel> Members,
    ImmutableArray<string> ConstructorMembers,
    string? ElementType,
    string? KeyType,
    string? ValueType,
    string? CustomCodecType,
    string? AdapterType,
    string? AdapterId,
    string WireFormatId,
    ImmutableArray<string> AssemblyDependencies,
    Location? Location)
{
    public bool ElementIsString { get; init; }
    public ulong CodecHashHigh { get; init; }
    public ulong CodecHashLow { get; init; }
}

internal readonly record struct GeneratedCodecHashModel(
    string TypeName,
    ulong High,
    ulong Low);

internal readonly record struct RpcHashValue(ulong High, ulong Low)