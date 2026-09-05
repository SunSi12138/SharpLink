namespace SharpLink.Generator;

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
    BuiltinAdapterOverride,
    CustomCodecBindingInvalid,
    CustomCodecTargetInvalid,
    CustomCodecTypeInvalid,
    CustomCodecIdentityInvalid,
    CustomCodecSelectionConflict,
    BuiltinCustomCodecOverride
}

internal readonly record struct DtoDiagnosticModel(
    DtoDiagnosticKind Kind,
    string TypeName,
    string Detail,
    Location? Location);

internal sealed record DtoGenerationResult(
    ImmutableArray<GeneratedCodecModel> Codecs,
    ImmutableArray<GeneratedCodecModel> ContractCodecs,
    ImmutableArray<string> FinalCodecBoundTypes,
    ImmutableArray<DtoDiagnosticModel> Diagnostics,
    ImmutableArray<GeneratedEnumModel> Enums)
{
    public DtoCodecAnalysisResult DtoAnalysis { get; } =
        RpcGenerator.CreateDtoCodecAnalysisResult(Codecs, ContractCodecs);
    public ImmutableArray<GeneratedCodecHashModel> CodecHashes { get; init; } =
        ImmutableArray<GeneratedCodecHashModel>.Empty;
    public ImmutableArray<GeneratedCodecHashModel> ReferencedCodecHashes { get; init; } =
        ImmutableArray<GeneratedCodecHashModel>.Empty;
    public ImmutableArray<GeneratedUnsafeBlitRequirementModel> UnsafeBlitRequirements { get; init; } =
        ImmutableArray<GeneratedUnsafeBlitRequirementModel>.Empty;
    public ImmutableArray<FinalCodecAutoLayoutDiagnosticModel> UnsafeBlitAutoLayoutDiagnostics { get; init; } =
        ImmutableArray<FinalCodecAutoLayoutDiagnosticModel>.Empty;
    public string AssemblyLogicalIdentity { get; init; } = string.Empty;
}

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
            x.FinalCodecBoundTypes.Length != y.FinalCodecBoundTypes.Length ||
            x.CodecHashes.Length != y.CodecHashes.Length ||
            x.ReferencedCodecHashes.Length != y.ReferencedCodecHashes.Length ||
            x.UnsafeBlitRequirements.Length != y.UnsafeBlitRequirements.Length ||
            x.UnsafeBlitAutoLayoutDiagnostics.Length != y.UnsafeBlitAutoLayoutDiagnostics.Length ||
            x.Diagnostics.Length != y.Diagnostics.Length || x.Enums.Length != y.Enums.Length ||
            !string.Equals(x.AssemblyLogicalIdentity, y.AssemblyLogicalIdentity, StringComparison.Ordinal))
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
        if (!x.FinalCodecBoundTypes.SequenceEqual(y.FinalCodecBoundTypes, StringComparer.Ordinal))
            return false;
        for (var index = 0; index < x.CodecHashes.Length; index++)
        {
            if (x.CodecHashes[index] != y.CodecHashes[index])
                return false;
        }
        for (var index = 0; index < x.ReferencedCodecHashes.Length; index++)
        {
            if (x.ReferencedCodecHashes[index] != y.ReferencedCodecHashes[index])
                return false;
        }
        for (var index = 0; index < x.UnsafeBlitRequirements.Length; index++)
        {
            if (x.UnsafeBlitRequirements[index] != y.UnsafeBlitRequirements[index])
                return false;
        }
        for (var index = 0; index < x.UnsafeBlitAutoLayoutDiagnostics.Length; index++)
        {
            var left = x.UnsafeBlitAutoLayoutDiagnostics[index];
            var right = y.UnsafeBlitAutoLayoutDiagnostics[index];
            if (!string.Equals(left.PayloadType, right.PayloadType, StringComparison.Ordinal) ||
                !string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
                !string.Equals(left.FieldPath, right.FieldPath, StringComparison.Ordinal))
            {
                return false;
            }
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
        hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(obj.AssemblyLogicalIdentity));
        foreach (var codec in obj.Codecs)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.SchemaId));
            hash = unchecked(hash * 31 + codec.CodecHashHigh.GetHashCode());
            hash = unchecked(hash * 31 + codec.CodecHashLow.GetHashCode());
        }
        foreach (var codec in obj.ContractCodecs)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codec.SchemaId));
            hash = unchecked(hash * 31 + codec.CodecHashHigh.GetHashCode());
            hash = unchecked(hash * 31 + codec.CodecHashLow.GetHashCode());
        }
        foreach (var type in obj.FinalCodecBoundTypes)
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(type));
        foreach (var codecHash in obj.CodecHashes)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codecHash.TypeName));
            hash = unchecked(hash * 31 + codecHash.High.GetHashCode());
            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());
            hash = unchecked(hash * 31 + codecHash.IsReferenced.GetHashCode());
        }
        foreach (var codecHash in obj.ReferencedCodecHashes)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codecHash.TypeName));
            hash = unchecked(hash * 31 + codecHash.High.GetHashCode());
            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());
        }
        foreach (var requirement in obj.UnsafeBlitRequirements)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(requirement.TypeName));
            hash = unchecked(hash * 31 + requirement.NativePointerWidth);
            hash = unchecked(hash * 31 + requirement.RequiresDateTimeOffsetRawAbi.GetHashCode());
        }
        foreach (var diagnostic in obj.UnsafeBlitAutoLayoutDiagnostics)
        {
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.PayloadType));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.TypeName));
            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.FieldPath));
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
            left.CodecHashHigh != right.CodecHashHigh ||
            left.CodecHashLow != right.CodecHashLow ||
            left.Kind != right.Kind || left.IsReferenceType != right.IsReferenceType ||
            !string.Equals(left.ElementType, right.ElementType, StringComparison.Ordinal) ||
            !string.Equals(left.KeyType, right.KeyType, StringComparison.Ordinal) ||
            !string.Equals(left.ValueType, right.ValueType, StringComparison.Ordinal) ||
            !string.Equals(left.CustomCodecType, right.CustomCodecType, StringComparison.Ordinal) ||
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

internal static class RpcHashValueExtensions
{
    internal static string ToHex(this RpcHashValue value)
        => value.High.ToString("x16", CultureInfo.InvariantCulture) +
           value.Low.ToString("x16", CultureInfo.InvariantCulture);
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
        => (long)Hash(iName.Replace("global::", "").Replace(" ", ""));

    public static string GetIdentifierHash(string value)
        => Hash(value).ToString("x16", CultureInfo.InvariantCulture);

    public static RpcHashValue GetSemanticHash(params string[] parts)
    {
        var canonical = new StringBuilder();
        foreach (var part in parts)
        {
            var value = part ?? string.Empty;
            canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        var hex = GetSha256(canonical.ToString());
        return new RpcHashValue(
            ulong.Parse(hex.Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(hex.Substring(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

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
