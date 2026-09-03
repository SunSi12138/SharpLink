from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"expected text not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, count))


Path('src/SharpLink.Abstractions/SharpLinkReferencedCodecDependency.cs').write_text('''namespace SharpLink.Abstractions;

/// <summary>
/// Binds a compile-time referenced generated Codec to the exact runtime target type and semantic hash
/// that the consuming generated assembly was compiled against.
/// </summary>
public sealed record SharpLinkReferencedCodecDependency(
    Type TargetType,
    RpcHash128 ExpectedCodecHash);

/// <summary>
/// Optional generated-manifest capability that publishes binding-aware referenced Codec dependencies.
/// The target <see cref="Type"/> preserves the exact assembly/load-context generation selected by the
/// consumer, while <see cref="SharpLinkReferencedCodecDependency.ExpectedCodecHash"/> locks the
/// expected generated Codec semantics.
/// </summary>
public interface ISharpLinkReferencedCodecDependencyManifest
{
    /// <summary>Gets the referenced generated Codec dependencies required by this manifest.</summary>
    IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; }
}
''')

replace(
    'src/SharpLink.Generator/RpcGenerator.Models.cs',
    '''internal readonly record struct GeneratedCodecHashModel(
    string TypeName,
    ulong High,
    ulong Low);''',
    '''internal readonly record struct GeneratedCodecHashModel(
    string TypeName,
    ulong High,
    ulong Low,
    bool IsReferenced = false);''')

replace(
    'src/SharpLink.Generator/RpcGenerator.CodecIdentity.cs',
    'return new GeneratedCodecHashModel(pair.Key, hash.High, hash.Low);',
    '''return new GeneratedCodecHashModel(
                        pair.Key,
                        hash.High,
                        hash.Low,
                        pair.Value is FinalReferencedCodecPlan);''')

replace(
    'src/SharpLink.Generator/RpcGenerator.DtoModels.cs',
    '''            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());
        }''',
    '''            hash = unchecked(hash * 31 + codecHash.Low.GetHashCode());
            hash = unchecked(hash * 31 + codecHash.IsReferenced.GetHashCode());
        }''')

emitter = 'src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs'
replace(
    emitter,
    '''        var contractDependencies = contractCodecs.SelectMany(static codec => codec.AssemblyDependencies)
            .Where(dependency => !dependencySet.Contains(dependency))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static dependency => dependency, StringComparer.Ordinal)
            .ToArray();
        var compileTimeDescriptor = BuildCompileTimeDescriptor(contracts, serviceModels, codecs, contractCodecs);''',
    '''        var contractDependencies = contractCodecs.SelectMany(static codec => codec.AssemblyDependencies)
            .Where(dependency => !dependencySet.Contains(dependency))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static dependency => dependency, StringComparer.Ordinal)
            .ToArray();
        var referencedCodecDependencies = codecHashes
            .Where(static codecHash => codecHash.IsReferenced)
            .OrderBy(static codecHash => codecHash.TypeName, StringComparer.Ordinal)
            .ToArray();
        var compileTimeDescriptor = BuildCompileTimeDescriptor(contracts, serviceModels, codecs, contractCodecs);''')
replace(
    emitter,
    '        sb.AppendLine($"public sealed partial class {manifestTypeName} : ISharpLinkGeneratedAssemblyManifest");',
    '        sb.AppendLine($"public sealed partial class {manifestTypeName} : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest");')
replace(
    emitter,
    '''        foreach (var dependency in contractDependencies)
            sb.AppendLine($"        \\"{EscapeString(dependency)}\\",");
        sb.AppendLine("    };");
        sb.AppendLine("    private static readonly IReadOnlyList<SharpLinkGeneratedContractDescriptor> __readOnlyContracts = Array.AsReadOnly(__contracts);");''',
    '''        foreach (var dependency in contractDependencies)
            sb.AppendLine($"        \\"{EscapeString(dependency)}\\",");
        sb.AppendLine("    };");
        sb.AppendLine("    private static readonly SharpLinkReferencedCodecDependency[] __referencedCodecDependencies = new SharpLinkReferencedCodecDependency[]");
        sb.AppendLine("    {");
        foreach (var dependency in referencedCodecDependencies)
        {
            sb.AppendLine("        new SharpLinkReferencedCodecDependency(");
            sb.AppendLine($"            typeof({dependency.TypeName}),");
            sb.AppendLine($"            new RpcHash128({dependency.High.ToString(InvariantCulture)}UL, {dependency.Low.ToString(InvariantCulture)}UL)),");
        }
        sb.AppendLine("    };");
        sb.AppendLine("    private static readonly IReadOnlyList<SharpLinkGeneratedContractDescriptor> __readOnlyContracts = Array.AsReadOnly(__contracts);");''')
replace(
    emitter,
    '''        sb.AppendLine("    private static readonly IReadOnlyList<string> __readOnlyContractDependencies = Array.AsReadOnly(__contractDependencies);");
        sb.AppendLine("    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => __readOnlyContracts;");''',
    '''        sb.AppendLine("    private static readonly IReadOnlyList<string> __readOnlyContractDependencies = Array.AsReadOnly(__contractDependencies);");
        sb.AppendLine("    private static readonly IReadOnlyList<SharpLinkReferencedCodecDependency> __readOnlyReferencedCodecDependencies = Array.AsReadOnly(__referencedCodecDependencies);");
        sb.AppendLine("    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => __readOnlyContracts;");''')
replace(
    emitter,
    '''        sb.AppendLine("    public IReadOnlyList<string> ContractDependencies => __readOnlyContractDependencies;");
        sb.AppendLine("}");''',
    '''        sb.AppendLine("    public IReadOnlyList<string> ContractDependencies => __readOnlyContractDependencies;");
        sb.AppendLine("    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies => __readOnlyReferencedCodecDependencies;");
        sb.AppendLine("}");''')

runtime = 'src/SharpLink.Runtime/SharpLinkRuntimeContext.cs'
replace(
    runtime,
    '                var owner = PrepareGeneratedManifest(manifest);',
    '                var owner = PrepareGeneratedManifest(manifest, validateReferencedDependencies: false);')
replace(
    runtime,
    '''            }
            PublishGeneratedCodecs(generatedRegistrations);''',
    '''            }
            ValidateReferencedCodecDependencies(prepared, generatedRegistrations);
            PublishGeneratedCodecs(generatedRegistrations);''')
replace(
    runtime,
    '''    internal RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(manifest);
        SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);
        SharpLinkGeneratedManifestStructureValidator.Validate(manifest);
        return RpcGeneratedManifestRegistration.Create(manifest, Codecs);
    }
''',
    '''    internal RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => PrepareGeneratedManifest(manifest, validateReferencedDependencies: true);

    private RpcGeneratedManifestRegistration PrepareGeneratedManifest(
        ISharpLinkGeneratedAssemblyManifest manifest,
        bool validateReferencedDependencies)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(manifest);
        SharpLinkGeneratedManifestCompatibility.ThrowIfIncompatible(manifest);
        SharpLinkGeneratedManifestStructureValidator.Validate(manifest);
        var registration = RpcGeneratedManifestRegistration.Create(manifest, Codecs);
        if (!validateReferencedDependencies)
            return registration;
        try
        {
            ValidateReferencedCodecDependencies([registration], CreateGeneratedCodecSnapshot());
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }
''')
replace(
    runtime,
    '''    internal void PublishGeneratedCodecs(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ((RpcCodecProvider)Codecs).PublishGeneratedRegistrations(registrations);
    }
''',
    '''    internal void PublishGeneratedCodecs(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RpcGeneratedManifestRegistration[] manifests;
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            manifests = [.. _manifestRegistrations];
        }
        ValidateReferencedCodecDependencies(manifests, registrations);
        ((RpcCodecProvider)Codecs).PublishGeneratedRegistrations(registrations);
    }

    private static void ValidateReferencedCodecDependencies(
        IEnumerable<RpcGeneratedManifestRegistration> manifests,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
    {
        foreach (var registration in manifests)
        {
            if (registration.Manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest)
                continue;
            var dependencies = dependencyManifest.ReferencedCodecDependencies
                ?? throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' returned null referenced Codec dependencies.");
            foreach (var dependency in dependencies)
            {
                if (dependency is null)
                {
                    throw new InvalidOperationException(
                        $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' contains a null referenced Codec dependency.");
                }
                var targetType = dependency.TargetType ?? throw new InvalidOperationException(
                    $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' contains a referenced Codec dependency with no target Type.");
                if (dependency.ExpectedCodecHash.IsEmpty)
                {
                    throw new InvalidOperationException(
                        $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' with an empty expected CodecHash.");
                }
                if (!registrations.TryGetValue(targetType, out var actual))
                {
                    throw new InvalidOperationException(
                        $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' from the exact bound runtime Type/assembly generation with expected CodecHash '{dependency.ExpectedCodecHash}', but no generated Codec is registered for that exact Type.");
                }
                if (!ReferenceEquals(actual.Owner.Manifest.OwnerAssembly, targetType.Assembly))
                {
                    throw new InvalidOperationException(
                        $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' from assembly generation '{targetType.Assembly.FullName}', but the registered Codec is owned by '{actual.Owner.Manifest.OwnerAssembly.FullName}'.");
                }
                if (actual.Factory.CodecHash != dependency.ExpectedCodecHash)
                {
                    throw new InvalidOperationException(
                        $"Generated manifest '{registration.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec '{targetType.FullName}' with expected CodecHash '{dependency.ExpectedCodecHash}', but the exact registered Type has CodecHash '{actual.Factory.CodecHash}'.");
                }
            }
        }
    }
''')

unit_tests = 'test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextTests.cs'
replace(
    unit_tests,
    '''    [Test]
    public void DisposedContextShouldRejectCodecResolution()''',
    '''    [Test]
    public void StaticBuildShouldRejectReferencedCodecHashMismatchBeforePublication()
    {
        var actualHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var expectedHash = new RpcHash128(0x3333333333333333UL, 0x4444444444444444UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), actualHash));
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);

        var failure = CaptureFailure(() =>
        {
            using var context = CreateRuntimeBuilder().Build(
                new ISharpLinkGeneratedAssemblyManifest[] { provider, consumer });
        });

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("expected CodecHash", StringComparison.Ordinal),
            "static bootstrap must reject a referenced Codec hash mismatch before publication");
    }

    [Test]
    public void DynamicPrepareShouldRejectReferencedCodecHashMismatch()
    {
        var actualHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var expectedHash = new RpcHash128(0x3333333333333333UL, 0x4444444444444444UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), actualHash));
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider });
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);

        var failure = CaptureFailure(() => context.PrepareGeneratedManifest(consumer));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("expected CodecHash", StringComparison.Ordinal),
            "dynamic manifest preparation must reject a referenced Codec hash mismatch");
    }

    [Test]
    public void CandidatePublicationShouldRejectRemovingReferencedCodecDependency()
    {
        var expectedHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), expectedHash));
        var consumer = new ReferencedCodecManifest(
            "referenced-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]);
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider, consumer });

        var failure = CaptureFailure(() => context.PublishGeneratedCodecs(
            new Dictionary<Type, RpcGeneratedCodecRegistration>()));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("no generated Codec is registered for that exact Type", StringComparison.Ordinal),
            "candidate publication must preserve reverse referenced Codec dependants");
    }

    [Test]
    public void DisposedContextShouldRejectCodecResolution()''')
replace(
    unit_tests,
    '''    private sealed class FixedNativeFactory<T>(IRpcCodec<T> codec) : IRpcGeneratedCodecFactory
    {''',
    '''    private sealed class HashedNativeFactory<T>(IRpcCodec<T> codec, RpcHash128 codecHash) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => codecHash;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? codec
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }

    private sealed class FixedNativeFactory<T>(IRpcCodec<T> codec) : IRpcGeneratedCodecFactory
    {''')
replace(
    unit_tests,
    '''    private sealed class TestManifest(string descriptor, params IRpcGeneratedCodecFactory[] codecs)
        : ISharpLinkGeneratedAssemblyManifest
    {''',
    '''    private sealed class ReferencedCodecManifest(
        string descriptor,
        SharpLinkReferencedCodecDependency[] referencedCodecDependencies)
        : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ReferencedCodecManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; } =
            referencedCodecDependencies;
    }

    private sealed class TestManifest(string descriptor, params IRpcGeneratedCodecFactory[] codecs)
        : ISharpLinkGeneratedAssemblyManifest
    {''')

generator_tests = 'test/SharpLink.Generator.Tests/RpcCodecTenthReviewRegressionTests.cs'
replace(
    generator_tests,
    '''        Ensure(
            !currentDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("runtime-sized intrinsic unmanaged types", StringComparison.Ordinal)),
            $"a current generated Codec identity must bypass pre-plan UnsafeBlit rejection even when the referenced unmanaged payload contains Vector<T>. Actual: {FormatDiagnostics(currentDiagnostics)}");
        return Task.CompletedTask;''',
    '''        Ensure(
            !currentDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("runtime-sized intrinsic unmanaged types", StringComparison.Ordinal)),
            $"a current generated Codec identity must bypass pre-plan UnsafeBlit rejection even when the referenced unmanaged payload contains Vector<T>. Actual: {FormatDiagnostics(currentDiagnostics)}");

        var currentManifest = RunGeneratorAndGetSources(consumer, sdk, current)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));
        Ensure(
            currentManifest.Contains("ISharpLinkReferencedCodecDependencyManifest", StringComparison.Ordinal) &&
            currentManifest.Contains("new SharpLinkReferencedCodecDependency(", StringComparison.Ordinal) &&
            currentManifest.Contains("typeof(global::Referenced.Payload)", StringComparison.Ordinal),
            "a FinalReferencedCodecPlan leaf must emit a binding-aware Type + CodecHash dependency descriptor");
        Ensure(
            !currentManifest.Contains("CurrentGeneratedPayload, Version=", StringComparison.Ordinal),
            "referenced Codec dependency provenance must not collapse back to an Assembly.FullName string");
        return Task.CompletedTask;''')
