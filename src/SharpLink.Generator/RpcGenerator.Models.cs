namespace SharpLink.Generator;

internal record RpcServiceModel(string ServiceName, string ServiceNamespace, string ServiceFullName, RpcInterfaceModel Interface);

internal record RpcInterfaceModel(
    string Name,
    string Namespace,
    string FullName,
    long Hash,
    EquatableArray<RpcMethodModel> Methods);

internal record RpcMethodModel(
    string Name,
    string ReturnType,
    bool IsGenericTask,
    bool IsStreamReturn,
    string? StreamItemType,
    string? GenericArgumentType,
    bool IsVoid,
    bool IsOneWay,
    bool HasCancellationToken,
    bool HasCallOptions,
    bool HasTimeoutAttribute,
    double? TimeoutSeconds,
    bool IsIdempotent,
    long Hash,
    EquatableArray<RpcParameterModel> Parameters);

internal record RpcParameterModel(
    string Name,
    string Type,
    bool IsStream,
    string? StreamItemType,
    bool IsBlittable,
    bool IsValueType,
    bool IsNullableReference,
    bool IsCancellationToken,
    bool IsCallOptions);

internal readonly record struct InvalidRpcMethodModel(string MethodName, string ReturnType, Location? Location);
internal readonly record struct InvalidCancellationTokenMethodModel(string MethodName, Location? Location);
internal readonly record struct InvalidCallOptionsMethodModel(string MethodName, Location? Location);
internal readonly record struct InvalidControlParameterOrderModel(string MethodName, Location? Location);
internal readonly record struct InvalidStreamCountMethodModel(string MethodName, int StreamParameterCount, Location? Location);
internal readonly record struct NonCancellableRpcMethodModel(string MethodName, Location? Location);
internal readonly record struct StreamingWithoutCancellationModel(string MethodName, Location? Location);
internal readonly record struct ConflictingCancellationContractModel(string MethodName, Location? Location);
internal readonly record struct InvalidGenericUsageModel(string SymbolName, string TypeName, Location? Location);
internal readonly record struct InvalidRpcContractInheritanceModel(string InterfaceName, Location? Location);

internal enum GeneratedCodecKind
{
    Dto,
    Array,
    List,
    Dictionary,
    Memory,
    ReadOnlyMemory,
    ImmutableArray,
    Nullable
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
    bool NonNullableReference,
    bool ConstructorBound,
    bool InitializerBound);

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
    string? ValueType);

internal enum DtoDiagnosticKind
{
    Unsupported,
    Cycle,
    MemberIdCollision,
    Constructor,
    Depth
}

internal readonly record struct DtoDiagnosticModel(
    DtoDiagnosticKind Kind,
    string TypeName,
    string Detail,
    Location? Location);

internal sealed record DtoGenerationResult(
    ImmutableArray<GeneratedCodecModel> Codecs,
    ImmutableArray<DtoDiagnosticModel> Diagnostics);

internal static class Hashing
{
    private const ulong FnvPrime = 1099511628211;
    private const ulong FnvOffsetBasis = 14695981039346656037;

    public static long GetMethodHash(string mName, string[] pNames)
    {
        var cleanP = string.Join(",", pNames).Replace("global::", "").Replace(" ", "");
        return (long)Hash($"{mName}({cleanP})");
    }

    public static long GetInterfaceHash(string iName)
    {
        return (long)Hash(iName.Replace("global::", "").Replace(" ", ""));
    }

    private static ulong Hash(string s)
    {
        ulong hash = FnvOffsetBasis;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }
}
