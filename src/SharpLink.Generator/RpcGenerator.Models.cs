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
    bool HasCallOptions,
    bool HasTimeoutAttribute,
    double? TimeoutSeconds,
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
    bool IsCallOptions,
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
internal readonly record struct InvalidCallOptionsMethodModel(string MethodName, Location? Location);
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
    Direct,
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
    string? AdapterType,
    string? AdapterId,
    string WireFormatId,
    ImmutableArray<string> AssemblyDependencies,
    Location? Location);

internal enum DtoDiagnosticKind
{
    Unsupported,
    Cycle,
    MemberIdCollision,
    Constructor,
    Depth,
    AdapterRegistrationInvalid,
    AdapterTypeInvalid,
    SelectorConflict,
    AdapterSelectionConflict,
    AdapterBindingInvalid,
    AdapterTargetInvalid,
    AdapterIdentityConflict,
    BuiltinAdapterOverride
}

internal readonly record struct DtoDiagnosticModel(
    DtoDiagnosticKind Kind,
    string TypeName,
    string Detail,
    Location? Location);

internal sealed record DtoGenerationResult(
    ImmutableArray<GeneratedCodecModel> Codecs,
    ImmutableArray<GeneratedCodecModel> ContractCodecs,
    ImmutableArray<GeneratedCodecModel> ContractManifestCodecs,
    ImmutableArray<DtoDiagnosticModel> Diagnostics,
    ImmutableArray<GeneratedEnumModel> Enums);

internal sealed record GeneratedEnumModel(
    string TypeName,
    string UnderlyingType,
    Location? Location);

internal sealed class DtoGenerationResultComparer : IEqualityComparer<DtoGenerationResult>
{
    internal static DtoGenerationResultComparer Instance { get; } = new();

    public bool Equals(DtoGenerationResult? x, DtoGenerationResult? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null || x.Codecs.Length != y.Codecs.Length ||
            x.ContractCodecs.Length != y.ContractCodecs.Length ||
            x.ContractManifestCodecs.Length != y.ContractManifestCodecs.Length ||
            x.Diagnostics.Length != y.Diagnostics.Length || x.Enums.Length != y.Enums.Length)
        {
            return false;
        }
        for (var index = 0; index < x.Codecs.Length; index++)
        {
            if (!CodecEquals(x.Codecs[index], y.Codecs[index]))
                return false;
        }
        for (var index = 0; index < x.ContractCodecs.Length; index++)
        {
            if (!CodecEquals(x.ContractCodecs[index], y.ContractCodecs[index]))
                return false;
        }
        for (var index = 0; index < x.ContractManifestCodecs.Length; index++)
        {
            if (!CodecEquals(x.ContractManifestCodecs[index], y.ContractManifestCodecs[index]))
                return false;
        }
        for (var index = 0; index < x.Diagnostics.Length; index++)
        {
            var left = x.Diagnostics[index];
            var right = y.Diagnostics[index];
            if (left.Kind != right.Kind ||
                !string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
                !string.Equals(left.Detail, right.Detail, StringComparison.Ordinal))
            {
                return false;
            }
        }
        for (var index = 0; index < x.Enums.Length; index++)
        {
            var left = x.Enums[index];
            var right = y.Enums[index];
            if (!string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
                !string.Equals(left.UnderlyingType, right.UnderlyingType, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public int GetHashCode(DtoGenerationResult obj)
    {
        var hash = 17;
        foreach (var codec in obj.Codecs)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.SchemaId));
        }
        foreach (var codec in obj.ContractCodecs)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.SchemaId));
        }
        foreach (var codec in obj.ContractManifestCodecs)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.SchemaId));
        }
        foreach (var diagnostic in obj.Diagnostics)
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.Detail));
        foreach (var item in obj.Enums)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(item.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(item.UnderlyingType));
        }
        return hash;
    }

    private static bool CodecEquals(GeneratedCodecModel left, GeneratedCodecModel right)
    {
        if (!string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
            !string.Equals(left.CodecName, right.CodecName, StringComparison.Ordinal) ||
            !string.Equals(left.SchemaId, right.SchemaId, StringComparison.Ordinal) ||
            left.Kind != right.Kind || left.IsReferenceType != right.IsReferenceType ||
            !string.Equals(left.ElementType, right.ElementType, StringComparison.Ordinal) ||
            !string.Equals(left.KeyType, right.KeyType, StringComparison.Ordinal) ||
            !string.Equals(left.ValueType, right.ValueType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterType, right.AdapterType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(left.WireFormatId, right.WireFormatId, StringComparison.Ordinal) ||
            !left.ConstructorMembers.SequenceEqual(right.ConstructorMembers, StringComparer.Ordinal) ||
            !left.AssemblyDependencies.SequenceEqual(right.AssemblyDependencies, StringComparer.Ordinal) ||
            left.Members.Length != right.Members.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Members.Length; index++)
        {
            var first = left.Members[index];
            var second = right.Members[index];
            if (first with { Location = null } != second with { Location = null })
                return false;
        }
        return true;
    }
}

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

    public static string GetIdentifierHash(string value)
        => Hash(value).ToString("x16", CultureInfo.InvariantCulture);

    public static string GetSha256(string value)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var hash = sha.ComputeHash(bytes);
            var result = new StringBuilder(hash.Length * 2);
            for (var index = 0; index < hash.Length; index++)
                result.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
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
