from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one anchor, found {count}: {old[:100]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


Path("src/SharpLink.Abstractions/SharpLinkGeneratedUnsafeBlitCatalog.cs").write_text(r'''using System.Runtime.CompilerServices;

namespace SharpLink.Abstractions;

/// <summary>Describes runtime ABI checks already resolved for one generated UnsafeBlit payload.</summary>
public readonly record struct SharpLinkGeneratedUnsafeBlitRequirement(
    int NativePointerWidth,
    bool RequiresDateTimeOffsetRawAbi);

/// <summary>
/// Publishes source-generated UnsafeBlit ABI requirements without retaining collectible payload Types.
/// </summary>
public static class SharpLinkGeneratedUnsafeBlitCatalog
{
    private static readonly ConditionalWeakTable<Type, RequirementBox> Requirements = new();

    /// <summary>Registers the resolved UnsafeBlit ABI requirement for one closed payload Type.</summary>
    public static void Register(
        Type targetType,
        int nativePointerWidth,
        bool requiresDateTimeOffsetRawAbi)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (nativePointerWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativePointerWidth));

        var incoming = new SharpLinkGeneratedUnsafeBlitRequirement(
            nativePointerWidth,
            requiresDateTimeOffsetRawAbi);
        var stored = Requirements.GetValue(targetType, _ => new RequirementBox(incoming));
        if (stored.Requirement != incoming)
        {
            throw new InvalidOperationException(
                $"Generated UnsafeBlit ABI requirements for '{targetType.FullName}' are inconsistent.");
        }
    }

    /// <summary>Attempts to read the generated UnsafeBlit ABI requirement for one closed payload Type.</summary>
    public static bool TryGet(
        Type targetType,
        out SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (Requirements.TryGetValue(targetType, out var stored))
        {
            requirement = stored.Requirement;
            return true;
        }

        requirement = default;
        return false;
    }

    private sealed class RequirementBox(SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        internal SharpLinkGeneratedUnsafeBlitRequirement Requirement { get; } = requirement;
    }
}
''', encoding="utf-8")

replace_once(
    "src/SharpLink.Generator/RpcGenerator.Models.cs",
    '''internal readonly record struct GeneratedCodecHashModel(\n    string TypeName,\n    ulong High,\n    ulong Low);\n\ninternal readonly record struct RpcHashValue(ulong High, ulong Low);''',
    '''internal readonly record struct GeneratedCodecHashModel(\n    string TypeName,\n    ulong High,\n    ulong Low);\n\ninternal readonly record struct GeneratedUnsafeBlitRequirementModel(\n    string TypeName,\n    int NativePointerWidth,\n    bool RequiresDateTimeOffsetRawAbi);\n\ninternal readonly record struct RpcHashValue(ulong High, ulong Low);''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.DtoModels.cs",
    '''    public ImmutableArray<GeneratedCodecHashModel> CodecHashes { get; init; } =\n        ImmutableArray<GeneratedCodecHashModel>.Empty;\n    public ImmutableArray<FinalCodecAutoLayoutDiagnosticModel> UnsafeBlitAutoLayoutDiagnostics { get; init; } =''',
    '''    public ImmutableArray<GeneratedCodecHashModel> CodecHashes { get; init; } =\n        ImmutableArray<GeneratedCodecHashModel>.Empty;\n    public ImmutableArray<GeneratedUnsafeBlitRequirementModel> UnsafeBlitRequirements { get; init; } =\n        ImmutableArray<GeneratedUnsafeBlitRequirementModel>.Empty;\n    public ImmutableArray<FinalCodecAutoLayoutDiagnosticModel> UnsafeBlitAutoLayoutDiagnostics { get; init; } =''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.DtoModels.cs",
    '''            x.FinalCodecBoundTypes.Length != y.FinalCodecBoundTypes.Length ||\n            x.CodecHashes.Length != y.CodecHashes.Length ||\n            x.UnsafeBlitAutoLayoutDiagnostics.Length != y.UnsafeBlitAutoLayoutDiagnostics.Length ||''',
    '''            x.FinalCodecBoundTypes.Length != y.FinalCodecBoundTypes.Length ||\n            x.CodecHashes.Length != y.CodecHashes.Length ||\n            x.UnsafeBlitRequirements.Length != y.UnsafeBlitRequirements.Length ||\n            x.UnsafeBlitAutoLayoutDiagnostics.Length != y.UnsafeBlitAutoLayoutDiagnostics.Length ||''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.DtoModels.cs",
    '''        for (var index = 0; index < x.CodecHashes.Length; index++)\n        {\n            if (x.CodecHashes[index] != y.CodecHashes[index])\n                return false;\n        }\n        for (var index = 0; index < x.UnsafeBlitAutoLayoutDiagnostics.Length; index++)''',
    '''        for (var index = 0; index < x.CodecHashes.Length; index++)\n        {\n            if (x.CodecHashes[index] != y.CodecHashes[index])\n                return false;\n        }\n        for (var index = 0; index < x.UnsafeBlitRequirements.Length; index++)\n        {\n            if (x.UnsafeBlitRequirements[index] != y.UnsafeBlitRequirements[index])\n                return false;\n        }\n        for (var index = 0; index < x.UnsafeBlitAutoLayoutDiagnostics.Length; index++)''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.DtoModels.cs",
    '''        foreach (var codecHash in obj.CodecHashes)\n        {\n            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codecHash.TypeName));\n            hash = unchecked(hash * 31 + codecHash.High.GetHashCode());\n            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());\n        }\n        foreach (var diagnostic in obj.UnsafeBlitAutoLayoutDiagnostics)''',
    '''        foreach (var codecHash in obj.CodecHashes)\n        {\n            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(codecHash.TypeName));\n            hash = unchecked(hash * 31 + codecHash.High.GetHashCode());\n            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());\n        }\n        foreach (var requirement in obj.UnsafeBlitRequirements)\n        {\n            hash = unchecked(hash * 31 + StringComparer.Ordinal.GetHashCode(requirement.TypeName));\n            hash = unchecked(hash * 31 + requirement.NativePointerWidth);\n            hash = unchecked(hash * 31 + requirement.RequiresDateTimeOffsetRawAbi.GetHashCode());\n        }\n        foreach (var diagnostic in obj.UnsafeBlitAutoLayoutDiagnostics)''')

Path("src/SharpLink.Generator/RpcGenerator.UnsafeBlitRequirements.cs").write_text(r'''namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<GeneratedUnsafeBlitRequirementModel> BuildUnsafeBlitRequirements(
        params FinalCodecGraph[] graphs)
        => graphs
            .SelectMany(static graph => graph.Plans.Values)
            .OfType<FinalUnsafeBlitCodecPlan>()
            .GroupBy(static plan => plan.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .Select(static plan => new GeneratedUnsafeBlitRequirementModel(
                plan.TypeName,
                plan.Abi.NativePointerWidth,
                RequiresDateTimeOffsetRawAbi(plan.Layout)))
            .OrderBy(static requirement => requirement.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

    private static bool RequiresDateTimeOffsetRawAbi(FinalPhysicalLayoutPlan plan)
        => plan switch
        {
            FinalPrimitivePhysicalPlan primitive =>
                primitive.FrameworkRawAbi?.StartsWith(
                    "framework-raw/datetimeoffset/",
                    StringComparison.Ordinal) == true,
            FinalEnumPhysicalPlan enumPlan => RequiresDateTimeOffsetRawAbi(enumPlan.Underlying),
            FinalFixedBufferPhysicalPlan buffer => RequiresDateTimeOffsetRawAbi(buffer.Element),
            FinalStructPhysicalPlan structure =>
                structure.Fields.Any(static field => RequiresDateTimeOffsetRawAbi(field.Layout)),
            _ => false
        };
}
''', encoding="utf-8")

Path("src/SharpLink.Generator/RpcGenerator.UnsafeBlitRequirementsEmitter.cs").write_text(r'''namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static string GenerateUnsafeBlitRequirements(
        ImmutableArray<GeneratedUnsafeBlitRequirementModel> requirements)
    {
        if (requirements.IsDefaultOrEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using SharpLink.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace SharpLink.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class __SharpLinkGeneratedUnsafeBlitRequirementsInitializer");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        foreach (var requirement in requirements.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            sb.AppendLine(
                $"        SharpLinkGeneratedUnsafeBlitCatalog.Register(typeof({requirement.TypeName}), {requirement.NativePointerWidth.ToString(InvariantCulture)}, {(requirement.RequiresDateTimeOffsetRawAbi ? "true" : "false")});");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
''', encoding="utf-8")

replace_once(
    "src/SharpLink.Generator/RpcGenerator.CodecPolicyOwnership.cs",
    '''        var unsafeBlitAutoLayoutDiagnostics =\n            DtoAnalysisState.BuildUnsafeBlitAutoLayoutDiagnostics(contractPolicyGraph);\n        var contractPolicyCodecs = AttachCodecHashes(''',
    '''        var unsafeBlitAutoLayoutDiagnostics =\n            DtoAnalysisState.BuildUnsafeBlitAutoLayoutDiagnostics(contractPolicyGraph);\n        var unsafeBlitRequirements = BuildUnsafeBlitRequirements(standaloneGraph, contractPolicyGraph);\n        var contractPolicyCodecs = AttachCodecHashes(''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.CodecPolicyOwnership.cs",
    '''            CodecHashes = codecHashes,\n            UnsafeBlitAutoLayoutDiagnostics = unsafeBlitAutoLayoutDiagnostics,''',
    '''            CodecHashes = codecHashes,\n            UnsafeBlitRequirements = unsafeBlitRequirements,\n            UnsafeBlitAutoLayoutDiagnostics = unsafeBlitAutoLayoutDiagnostics,''')

replace_once(
    "src/SharpLink.Generator/RpcGenerator.cs",
    '''            if (!result.Codecs.IsDefaultOrEmpty || !result.ContractCodecs.IsDefaultOrEmpty)\n            {\n                spc.AddSource(\n                    "SharpLink.GeneratedCodecs.g.cs",\n                    SourceText.From(GenerateCodecs(result.Codecs.AddRange(result.ContractCodecs)), Encoding.UTF8));\n            }\n        });''',
    '''            if (!result.Codecs.IsDefaultOrEmpty || !result.ContractCodecs.IsDefaultOrEmpty)\n            {\n                spc.AddSource(\n                    "SharpLink.GeneratedCodecs.g.cs",\n                    SourceText.From(GenerateCodecs(result.Codecs.AddRange(result.ContractCodecs)), Encoding.UTF8));\n            }\n\n            if (!result.UnsafeBlitRequirements.IsDefaultOrEmpty)\n            {\n                spc.AddSource(\n                    "SharpLink.GeneratedUnsafeBlitRequirements.g.cs",\n                    SourceText.From(GenerateUnsafeBlitRequirements(result.UnsafeBlitRequirements), Encoding.UTF8));\n            }\n        });''')

Path("src/SharpLink.Runtime/Codec/RpcUnsafeBlitPlatform.cs").write_text(r'''using System.Buffers.Binary;
#if !SHARPLINK_NATIVEAOT
using System.Reflection;
#endif
using System.Runtime.InteropServices;

namespace SharpLink.Runtime;

internal static class RpcUnsafeBlitPlatform
{
    private const int SupportedNativePointerSize = 8;
    private static readonly bool DateTimeOffsetRawAbiSupported = ProbeDateTimeOffsetRawAbi();

    internal static void EnsureSupported(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (SharpLinkGeneratedUnsafeBlitCatalog.TryGet(targetType, out var generatedRequirement))
        {
            if (!IsSupported(generatedRequirement, IntPtr.Size, DateTimeOffsetRawAbiSupported))
            {
                throw new PlatformNotSupportedException(
                    $"UnsafeBlit Codec for '{targetType.FullName}' does not satisfy its source-generated runtime ABI requirement.");
            }
            return;
        }

#if SHARPLINK_NATIVEAOT
        throw new PlatformNotSupportedException(
            $"UnsafeBlit Codec for '{targetType.FullName}' requires source-generated ABI metadata under NativeAOT. " +
            "Use the type in a generated RPC contract or bind an explicit Codec/Adapter.");
#else
        if (ContainsRuntimeSizedMember(targetType, new HashSet<Type>()))
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' contains runtime-sized members and does not have a stable wire layout.");
        }
        if (IntPtr.Size != SupportedNativePointerSize)
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' requires the SharpLink 64-bit wire ABI.");
        }
        if (!DateTimeOffsetRawAbiSupported && ContainsDateTimeOffset(targetType, new HashSet<Type>()))
        {
            throw new PlatformNotSupportedException(
                $"UnsafeBlit Codec for '{targetType.FullName}' contains DateTimeOffset, whose raw representation does not match the SharpLink declared framework ABI on this runtime.");
        }
#endif
    }

    internal static bool IsSupported(Type targetType, int nativePointerSize)
        => IsSupported(targetType, nativePointerSize, DateTimeOffsetRawAbiSupported);

    internal static bool IsSupported(
        Type targetType,
        int nativePointerSize,
        bool dateTimeOffsetRawAbiSupported)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (SharpLinkGeneratedUnsafeBlitCatalog.TryGet(targetType, out var generatedRequirement))
            return IsSupported(generatedRequirement, nativePointerSize, dateTimeOffsetRawAbiSupported);

#if SHARPLINK_NATIVEAOT
        return false;
#else
        return nativePointerSize == SupportedNativePointerSize &&
               !ContainsRuntimeSizedMember(targetType, new HashSet<Type>()) &&
               (dateTimeOffsetRawAbiSupported || !ContainsDateTimeOffset(targetType, new HashSet<Type>()));
#endif
    }

    private static bool IsSupported(
        SharpLinkGeneratedUnsafeBlitRequirement requirement,
        int nativePointerSize,
        bool dateTimeOffsetRawAbiSupported)
        => nativePointerSize == requirement.NativePointerWidth &&
           (!requirement.RequiresDateTimeOffsetRawAbi || dateTimeOffsetRawAbiSupported);

#if !SHARPLINK_NATIVEAOT
    private static bool ContainsRuntimeSizedMember(Type type, HashSet<Type> seen)
    {
        if (IsRuntimeSizedIntrinsic(type))
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;
        if (!seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsRuntimeSizedMember(field.FieldType, seen))
                return true;
        }

        return false;
    }

    private static bool ContainsDateTimeOffset(Type type, HashSet<Type> seen)
    {
        if (type == typeof(DateTimeOffset))
            return true;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum || !seen.Add(type))
            return false;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (ContainsDateTimeOffset(field.FieldType, seen))
                return true;
        }
        return false;
    }

    private static bool IsRuntimeSizedIntrinsic(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Numerics.Vector<>);
#endif

    private static bool ProbeDateTimeOffsetRawAbi()
    {
        var value = new DateTimeOffset(2026, 8, 31, 13, 45, 12, TimeSpan.FromMinutes(330));
        var raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        if (raw.Length != 16)
            return false;

        Span<byte> expected = stackalloc byte[16];
        BinaryPrimitives.WriteInt16LittleEndian(expected, 330);
        BinaryPrimitives.WriteInt64LittleEndian(expected.Slice(8), value.UtcTicks);
        return raw.SequenceEqual(expected);
    }
}
''', encoding="utf-8")

replace_once(
    "src/SharpLink.Runtime/SharpLink.Runtime.csproj",
    '''        <TargetFramework>net10.0</TargetFramework>\n        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>''',
    '''        <TargetFramework>net10.0</TargetFramework>\n        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>\n        <DefineConstants Condition="'$(PublishAot)' == 'true'">$(DefineConstants);SHARPLINK_NATIVEAOT</DefineConstants>''')

Path("test/SharpLink.UnitTests/Abstractions/GeneratedUnsafeBlitCatalogTests.cs").write_text(r'''using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Abstractions;

public sealed class GeneratedUnsafeBlitCatalogTests
{
    [Test]
    public void RequirementRegistrationShouldBeWeakKeyedAndDeterministic()
    {
        SharpLinkGeneratedUnsafeBlitCatalog.Register(
            typeof(CatalogPayload),
            nativePointerWidth: 8,
            requiresDateTimeOffsetRawAbi: true);
        SharpLinkGeneratedUnsafeBlitCatalog.Register(
            typeof(CatalogPayload),
            nativePointerWidth: 8,
            requiresDateTimeOffsetRawAbi: true);

        if (!SharpLinkGeneratedUnsafeBlitCatalog.TryGet(typeof(CatalogPayload), out var requirement) ||
            requirement.NativePointerWidth != 8 ||
            !requirement.RequiresDateTimeOffsetRawAbi)
        {
            throw new InvalidOperationException("Generated UnsafeBlit requirement was not retained accurately.");
        }

        try
        {
            SharpLinkGeneratedUnsafeBlitCatalog.Register(
                typeof(CatalogPayload),
                nativePointerWidth: 4,
                requiresDateTimeOffsetRawAbi: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Conflicting generated UnsafeBlit requirements must fail closed.");
    }

    private readonly record struct CatalogPayload(DateTimeOffset Value);
}
''', encoding="utf-8")

replace_once(
    "doc/contracts-and-codecs.md",
    '''涉及 unsafe/native/uninitialized 来源或机密边界时，可靠的支持路径是为该 **user-defined payload type** 显式绑定 field-wise/non-raw representation 的自定义 Codec/Adapter，而不是依赖调用方先清 padding 后再经过可能发生的 struct copy。完整边界见 [UnsafeBlit padding 安全评估](unsafe-blit-padding-security.md)；跨运行时 ABI/兼容性范围见 [UnsafeBlit 兼容性](codec-compatibility.md)。这里描述的是 RPC payload Codec，不改变 SharpLink 自身协议 framing 字段的编码。''',
    '''涉及 unsafe/native/uninitialized 来源或机密边界时，可靠的支持路径是为该 **user-defined payload type** 显式绑定 field-wise/non-raw representation 的自定义 Codec/Adapter，而不是依赖调用方先清 padding 后再经过可能发生的 struct copy。完整边界见 [UnsafeBlit padding 安全评估](unsafe-blit-padding-security.md)；跨运行时 ABI/兼容性范围见 [UnsafeBlit 兼容性](codec-compatibility.md)。这里描述的是 RPC payload Codec，不改变 SharpLink 自身协议 framing 字段的编码。\n\nNativeAOT 不会在运行时重新反射 UnsafeBlit payload 的字段图。Generator 从最终 `FinalUnsafeBlitCodecPlan` 直接发布 native-pointer width 与 framework raw-ABI requirement；Runtime 只验证这份 resolved metadata。没有 source-generated ABI metadata 的任意 unmanaged fallback 在 NativeAOT 下 fail-closed，JIT runtime 则保留运行时字段图检查。''')
