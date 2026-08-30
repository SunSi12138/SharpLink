from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    target = Path(path)
    text = target.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one replacement target, found {count}")
    target.write_text(text.replace(old, new), encoding="utf-8")


replace_once(
    "src/SharpLink.Generator/RpcGenerator.Models.cs",
    """internal sealed record DtoGenerationResult(
    ImmutableArray<GeneratedCodecModel> Codecs,
    ImmutableArray<GeneratedCodecModel> ContractCodecs,
    ImmutableArray<string> FinalCodecBoundTypes,
    ImmutableArray<DtoDiagnosticModel> Diagnostics,
    ImmutableArray<GeneratedEnumModel> Enums)
{
    public ImmutableArray<GeneratedCodecHashModel> CodecHashes { get; init; } =
        ImmutableArray<GeneratedCodecHashModel>.Empty;
}
""",
    """internal sealed record DtoGenerationResult(
    ImmutableArray<GeneratedCodecModel> Codecs,
    ImmutableArray<GeneratedCodecModel> ContractCodecs,
    ImmutableArray<string> FinalCodecBoundTypes,
    ImmutableArray<DtoDiagnosticModel> Diagnostics,
    ImmutableArray<GeneratedEnumModel> Enums)
{
    public ImmutableArray<GeneratedCodecHashModel> CodecHashes { get; init; } =
        ImmutableArray<GeneratedCodecHashModel>.Empty;
    public string AssemblyLogicalIdentity { get; init; } = string.Empty;
}
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.Models.cs",
    """            x.FinalCodecBoundTypes.Length != y.FinalCodecBoundTypes.Length ||
            x.CodecHashes.Length != y.CodecHashes.Length ||
            x.Diagnostics.Length != y.Diagnostics.Length || x.Enums.Length != y.Enums.Length)
""",
    """            x.FinalCodecBoundTypes.Length != y.FinalCodecBoundTypes.Length ||
            x.CodecHashes.Length != y.CodecHashes.Length ||
            x.Diagnostics.Length != y.Diagnostics.Length || x.Enums.Length != y.Enums.Length ||
            !string.Equals(x.AssemblyLogicalIdentity, y.AssemblyLogicalIdentity, StringComparison.Ordinal))
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.Models.cs",
    """    public int GetHashCode(DtoGenerationResult obj)
    {
        var hash = 17;
""",
    """    public int GetHashCode(DtoGenerationResult obj)
    {
        var hash = 17;
        hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(obj.AssemblyLogicalIdentity));
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.CodecPolicyOwnership.cs",
    """        {
            CodecHashes = codecHashes
        };
""",
    """        {
            CodecHashes = codecHashes,
            AssemblyLogicalIdentity = compilation.Assembly.Identity.Name
        };
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.cs",
    """            var code = GenerateAssemblyManifest(interfaces, services, codecs, contractCodecs, codecHashes);
""",
    """            var code = GenerateAssemblyManifest(
                interfaces,
                services,
                codecs,
                contractCodecs,
                codecHashes,
                value.Right.AssemblyLogicalIdentity);
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs",
    """        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecModel> contractCodecs,
        ImmutableArray<GeneratedCodecHashModel> codecHashes)
""",
    """        ImmutableArray<GeneratedCodecModel> codecs,
        ImmutableArray<GeneratedCodecModel> contractCodecs,
        ImmutableArray<GeneratedCodecHashModel> codecHashes,
        string assemblyLogicalIdentity)
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs",
    """        var rpcIdentity = BuildRpcAssemblyIdentity(contracts, codecHashes);
""",
    """        var rpcIdentity = BuildRpcAssemblyIdentity(assemblyLogicalIdentity, contracts, codecHashes);
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.RpcIdentity.cs",
    """    private static RpcAssemblyIdentityModel BuildRpcAssemblyIdentity(
        RpcInterfaceModel[] contracts,
        ImmutableArray<GeneratedCodecHashModel> codecHashes)
""",
    """    private static RpcAssemblyIdentityModel BuildRpcAssemblyIdentity(
        string assemblyLogicalIdentity,
        RpcInterfaceModel[] contracts,
        ImmutableArray<GeneratedCodecHashModel> codecHashes)
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.RpcIdentity.cs",
    """        var assemblyParts = new List<string>
        {
            "rpc-assembly/v1",
            contractIdentities.Length.ToString(InvariantCulture)
        };
""",
    """        var assemblyParts = new List<string>
        {
            "rpc-assembly/v1",
            assemblyLogicalIdentity,
            contractIdentities.Length.ToString(InvariantCulture)
        };
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.CodecIdentity.cs",
    """            if (type is IPointerTypeSymbol pointer)
            {
                builder.Append("|pointer|");
                AppendUnsafeBlitPhysicalLayout(pointer.PointedAtType, builder, stack);
                return;
            }
            if (type is IFunctionPointerTypeSymbol)
            {
                builder.Append("|function-pointer");
                return;
            }
""",
    """            if (type is IPointerTypeSymbol pointer)
            {
                builder.Append("|native-pointer-width/64|pointer|");
                AppendUnsafeBlitPhysicalLayout(pointer.PointedAtType, builder, stack);
                return;
            }
            if (type is IFunctionPointerTypeSymbol)
            {
                builder.Append("|native-pointer-width/64|function-pointer");
                return;
            }
""",
)

replace_once(
    "src/SharpLink.Generator/RpcGenerator.CodecIdentity.cs",
    """                SpecialType.System_Int64 => "i64",
                SpecialType.System_UInt64 => "u64",
                SpecialType.System_Double => "f64",
""",
    """                SpecialType.System_Int64 => "i64",
                SpecialType.System_UInt64 => "u64",
                SpecialType.System_IntPtr => "native-pointer-width/64:intptr",
                SpecialType.System_UIntPtr => "native-pointer-width/64:uintptr",
                SpecialType.System_Double => "f64",
""",
)

replace_once(
    "src/SharpLink.Runtime/Codec/RpcCodecProvider.cs",
    """            if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return UnsafeBlitCodec<T>.Instance;
""",
    """            if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                RpcUnsafeBlitPlatform.EnsureSupported(targetType);
                return UnsafeBlitCodec<T>.Instance;
            }
""",
)

helper = Path("src/SharpLink.Runtime/Codec/RpcUnsafeBlitPlatform.cs")
if helper.exists():
    raise SystemExit(f"{helper}: file already exists")
helper.write_text(
    """using System.Reflection;

namespace SharpLink.Runtime;

internal static class RpcUnsafeBlitPlatform
{
    private const int SupportedNativePointerSize = 8;

    internal static void EnsureSupported(Type targetType)
    {
        if (IsSupported(targetType, IntPtr.Size))
            return;

        throw new PlatformNotSupportedException(
            $"UnsafeBlit Codec for '{targetType.FullName}' contains native-sized members and requires a 64-bit process.");
    }

    internal static bool IsSupported(Type targetType, int nativePointerSize)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return nativePointerSize == SupportedNativePointerSize ||
               !ContainsNativeSizedMember(targetType, new HashSet<Type>());
    }

    private static bool ContainsNativeSizedMember(Type type, HashSet<Type> seen)
    {
        if (type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsPointer || type.IsFunctionPointer)
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;
        if (!seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsNativeSizedMember(field.FieldType, seen))
                return true;
        }

        return false;
    }
}
""",
    encoding="utf-8",
)

unit_test = Path("test/SharpLink.UnitTests/Runtime/RpcUnsafeBlitPlatformTests.cs")
if unit_test.exists():
    raise SystemExit(f"{unit_test}: file already exists")
unit_test.write_text(
    """using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcUnsafeBlitPlatformTests
{
    [Test]
    public void NativeSizedUnsafeBlitShouldBe64BitOnly()
    {
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 8),
            "native-sized UnsafeBlit payloads must be accepted by the supported 64-bit runtime");
        Ensure(
            !RpcUnsafeBlitPlatform.IsSupported(typeof(NativeSizedPayload), 4),
            "native-sized UnsafeBlit payloads must be rejected by a 32-bit runtime");
        Ensure(
            RpcUnsafeBlitPlatform.IsSupported(typeof(PortablePayload), 4),
            "fixed-width UnsafeBlit payloads must remain valid on a 32-bit runtime");
    }

    private struct NativeSizedPayload
    {
        public int Prefix;
        public nint Handle;
    }

    private struct PortablePayload
    {
        public int Prefix;
        public long Value;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
""",
    encoding="utf-8",
)

identity_tests = Path("test/SharpLink.Generator.Tests/RpcDeterministicIdentityTests.cs")
text = identity_tests.read_text(encoding="utf-8")
marker = """    [Test]
    public Task DtoWireShapeChangeShouldChangeFinalRpcIdentity()
"""
if text.count(marker) != 1:
    raise SystemExit("identity tests: assembly regression insertion marker mismatch")
assembly_test = """    [Test]
    public Task SameApparentAbiInDifferentAssembliesShouldHaveDifferentAssemblyIdentity()
    {
        var source = BuildDtoIdentitySource(includeExtraMember: false, idempotent: false);
        var first = GenerateIdentityManifest(
            "DeterministicIdentityAssemblyA",
            source,
            Platform.AnyCpu);
        var second = GenerateIdentityManifest(
            "DeterministicIdentityAssemblyB",
            source,
            Platform.AnyCpu);

        Ensure(
            ExtractGeneratedCodecIdentity(first, "DeterministicPayload") ==
            ExtractGeneratedCodecIdentity(second, "DeterministicPayload"),
            "the same payload definition must retain the same CodecHash across Contract assemblies");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(first) != ExtractGeneratedRpcAssemblyHash(second),
            "different Contract assembly logical identities must not collapse to one RpcAssemblyHash");
        return Task.CompletedTask;
    }

"""
text = text.replace(marker, assembly_test + marker)

marker = """    [Test]
    public Task SharedPayloadShouldHaveSameCodecHashAcrossContractAssemblies()
"""
if text.count(marker) != 1:
    raise SystemExit("identity tests: native-size regression insertion marker mismatch")
native_test = """    [Test]
    public Task NativeSizedUnsafeBlitShouldUseStable64BitOnlyIdentity()
    {
        var nativeSource = BuildSource("""
public struct NativeSizedUnsafeLayoutPayload
{
    public int Prefix;
    public nint Handle;
}

[SharpLink.Sdk.RpcContract]
public interface INativeSizedUnsafeLayoutContract : SharpLink.Sdk.IService
{
    ValueTask<NativeSizedUnsafeLayoutPayload> Echo(
        NativeSizedUnsafeLayoutPayload value,
        CancellationToken cancellationToken);
}
""");
        var fixed64Source = nativeSource.Replace("public nint Handle;", "public long Handle;", StringComparison.Ordinal);

        var x64 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            nativeSource,
            Platform.X64);
        var x86 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            nativeSource,
            Platform.X86);
        var fixed64 = GenerateIdentityManifest(
            "NativeSizedUnsafeLayoutIdentity",
            fixed64Source,
            Platform.X64);

        Ensure(
            ExtractGeneratedRpcAssemblyHash(x64) == ExtractGeneratedRpcAssemblyHash(x86),
            "native-sized UnsafeBlit identity must describe the supported 64-bit wire layout independently of compiler platform");
        Ensure(
            ExtractGeneratedRpcAssemblyHash(x64) != ExtractGeneratedRpcAssemblyHash(fixed64),
            "native-sized UnsafeBlit identity must remain distinct from a fixed-width Int64 field");
        return Task.CompletedTask;
    }

"""
text = text.replace(marker, native_test + marker)
identity_tests.write_text(text, encoding="utf-8")
