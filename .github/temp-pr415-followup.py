from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise RuntimeError(f"pattern not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1))


def remove_once(path, block):
    replace_once(path, block, "")


# Shared binding-aware resolution for legacy string-shaped generated module dependencies.
Path("src/SharpLink.Runtime/GeneratedAssembly/SharpLinkGeneratedDependencyBinding.cs").write_text(r'''using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedDependencyBinding
{
    internal static Assembly? Resolve(Assembly ownerAssembly, string dependencyIdentity)
    {
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        if (string.IsNullOrWhiteSpace(dependencyIdentity))
            return null;
        if (string.Equals(ownerAssembly.FullName, dependencyIdentity, StringComparison.Ordinal))
            return ownerAssembly;

        AssemblyName requested;
        try
        {
            requested = new AssemblyName(dependencyIdentity);
        }
        catch (Exception exception) when (exception is ArgumentException or FileLoadException)
        {
            return null;
        }

        AssemblyName? reference = null;
        foreach (var candidate in ownerAssembly.GetReferencedAssemblies())
        {
            if (!AssemblyName.ReferenceMatchesDefinition(candidate, requested))
                continue;
            reference = candidate;
            break;
        }
        if (reference is null)
            return null;

        var loadContext = AssemblyLoadContext.GetLoadContext(ownerAssembly);
        if (loadContext is null)
            return null;
        foreach (var loaded in loadContext.Assemblies)
        {
            if (AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), reference))
                return loaded;
        }

        try
        {
            return loadContext.LoadFromAssemblyName(reference);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    internal static bool Matches(
        Assembly ownerAssembly,
        string dependencyIdentity,
        Assembly candidateAssembly)
        => ReferenceEquals(Resolve(ownerAssembly, dependencyIdentity), candidateAssembly);
}
''')

# Contract-scoped provider: typed referenced dependencies dominate legacy module strings and fail closed.
provider = "src/SharpLink.Runtime/Codec/RpcManifestCodecProvider.cs"
replace_once(provider, r'''        if (_runtimeProvider is not null &&
            _runtimeProvider.CreateGeneratedRegistrationSnapshot().TryGetValue(targetType, out var dependency) &&
            IsGeneratedDependencyAllowed(targetType, dependency))
        {
            return ResolveOwned<T>(targetType, dependency);
        }

        if (BuiltinRpcCodecs.TryGet(targetType, out var builtin))
''', r'''        var referencedDependency = FindReferencedCodecDependency(targetType);
        if (_runtimeProvider is not null &&
            _runtimeProvider.CreateGeneratedRegistrationSnapshot().TryGetValue(targetType, out var dependency) &&
            IsGeneratedDependencyAllowed(targetType, dependency, referencedDependency))
        {
            return ResolveOwned<T>(targetType, dependency);
        }
        if (referencedDependency is not null)
        {
            throw new InvalidOperationException(
                $"Contract assembly '{_owner.Manifest.OwnerAssembly.FullName}' requires referenced generated Codec " +
                $"'{targetType.FullName}' from the exact bound runtime Type/assembly generation with CodecHash " +
                $"'{referencedDependency.ExpectedCodecHash}', but that exact generated registration is not available.");
        }

        if (BuiltinRpcCodecs.TryGet(targetType, out var builtin))
''')
replace_once(provider, r'''    private bool IsGeneratedDependencyAllowed(
        Type targetType,
        RpcGeneratedCodecRegistration registration)
    {
        if (ReferenceEquals(registration.Owner, _owner))
            return true;

        var dependencyAssembly = registration.Owner.Manifest.OwnerAssembly;
        var dependencyIdentity = dependencyAssembly.FullName;
        if (dependencyIdentity is null || !IsTargetOwnedByDependency(targetType, dependencyAssembly))
            return false;

        if (ContainsIdentity(_owner.Manifest.ContractDependencies, dependencyIdentity))
            return true;

        // Compatibility for custom manifests that predate ContractDependencies and publish their
        // whole generated-module closure through Dependencies.
        return ContainsIdentity(_owner.Manifest.Dependencies, dependencyIdentity);
    }

    private static bool ContainsIdentity(IReadOnlyList<string> dependencies, string identity)
    {
        for (var index = 0; index < dependencies.Count; index++)
        {
            if (string.Equals(dependencies[index], identity, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
''', r'''    private bool IsGeneratedDependencyAllowed(
        Type targetType,
        RpcGeneratedCodecRegistration registration,
        SharpLinkReferencedCodecDependency? referencedDependency)
    {
        if (referencedDependency is not null)
        {
            return ReferenceEquals(referencedDependency.TargetType, targetType) &&
                   ReferenceEquals(registration.Owner.Manifest.OwnerAssembly, targetType.Assembly) &&
                   registration.Factory.CodecHash == referencedDependency.ExpectedCodecHash;
        }

        if (ReferenceEquals(registration.Owner, _owner))
            return true;

        var dependencyAssembly = registration.Owner.Manifest.OwnerAssembly;
        if (!IsTargetOwnedByDependency(targetType, dependencyAssembly))
            return false;

        if (ContainsBoundDependency(_owner.Manifest.ContractDependencies, dependencyAssembly))
            return true;

        // Compatibility for custom manifests that predate ContractDependencies and publish their
        // whole generated-module closure through Dependencies. The string is only a CLR AssemblyRef
        // locator; the actual permission is bound to the resolved Assembly object/generation.
        return ContainsBoundDependency(_owner.Manifest.Dependencies, dependencyAssembly);
    }

    private SharpLinkReferencedCodecDependency? FindReferencedCodecDependency(Type targetType)
    {
        if (_owner.Manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest)
            return null;
        var dependencies = dependencyManifest.ReferencedCodecDependencies;
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            if (dependency is not null && ReferenceEquals(dependency.TargetType, targetType))
                return dependency;
        }
        return null;
    }

    private bool ContainsBoundDependency(
        IReadOnlyList<string> dependencies,
        Assembly dependencyAssembly)
    {
        for (var index = 0; index < dependencies.Count; index++)
        {
            if (SharpLinkGeneratedDependencyBinding.Matches(
                    _owner.Manifest.OwnerAssembly,
                    dependencies[index],
                    dependencyAssembly))
            {
                return true;
            }
        }
        return false;
    }
''')

# Final codec candidate validation must include an incoming not-yet-adopted manifest.
runtime = "src/SharpLink.Runtime/SharpLinkRuntimeContext.cs"
replace_once(runtime, r'''    internal void PublishGeneratedCodecs(IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations)
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
''', r'''    internal void PublishGeneratedCodecs(
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> registrations,
        RpcGeneratedManifestRegistration? pendingRegistration = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RpcGeneratedManifestRegistration[] manifests;
        lock (_registrationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            manifests = [.. _manifestRegistrations];
        }
        ValidateReferencedCodecDependencies(manifests, registrations);
        if (pendingRegistration is not null)
            ValidateReferencedCodecDependencies([pendingRegistration], registrations);
        ((RpcCodecProvider)Codecs).PublishGeneratedRegistrations(registrations);
    }
''')

# Client: include pending manifest in final snapshot validation and bind module strings to exact Assembly objects.
client = "src/SharpLink.Client/SharpLinkClient.AssemblyRegistration.cs"
text = Path(client).read_text()
if text.count("_runtimeContext.PublishGeneratedCodecs(candidate.Codecs);") != 2:
    raise RuntimeError("unexpected client candidate publish count")
text = text.replace(
    "_runtimeContext.PublishGeneratedCodecs(candidate.Codecs);",
    "_runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);"
)
Path(client).write_text(text)
replace_once(client, r'''                        _dynamicModules.Add(newAssembly, newModule);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
''', r'''                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _dynamicModules.Add(newAssembly, newModule);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
''')
replace_once(client, r'''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        var identity = ownerAssembly.FullName;
        if (identity is not null && EnumerateManifestDependencies(manifest)
            .Any(dependency => string.Equals(dependency, identity, StringComparison.Ordinal)))
        {
            return true;
        }

        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }

        return referencedDependencies.Any(dependency =>
            dependency is not null &&
            dependency.TargetType is { } targetType &&
            ReferenceEquals(targetType.Assembly, ownerAssembly));
    }

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly.FullName ?? string.Empty);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly.FullName ?? string.Empty);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(dependency, self, StringComparison.Ordinal) || available.Contains(dependency))
                continue;
            return CreateError(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must be registered and running before '{self}'.",
                incoming.OwnerAssembly, "Dependency");
        }
        return null;
    }
''', r'''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        foreach (var dependency in EnumerateManifestDependencies(manifest))
        {
            if (SharpLinkGeneratedDependencyBinding.Matches(
                    manifest.OwnerAssembly,
                    dependency,
                    ownerAssembly))
            {
                return true;
            }
        }

        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }

        return referencedDependencies.Any(dependency =>
            dependency is not null &&
            dependency.TargetType is { } targetType &&
            ReferenceEquals(targetType.Assembly, ownerAssembly));
    }

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            var boundAssembly = SharpLinkGeneratedDependencyBinding.Resolve(
                incoming.OwnerAssembly,
                dependency);
            if (ReferenceEquals(boundAssembly, incoming.OwnerAssembly) ||
                boundAssembly is not null && available.Contains(boundAssembly))
            {
                continue;
            }
            return CreateError(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must resolve through '{self}' to the exact registered and running Assembly generation before registration.",
                incoming.OwnerAssembly, "Dependency");
        }
        return null;
    }
''')

# Server mirrors client identity and pending-manifest behavior.
server = "src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs"
text = Path(server).read_text()
if text.count("_runtimeContext.PublishGeneratedCodecs(candidate.Codecs);") != 2:
    raise RuntimeError("unexpected server candidate publish count")
text = text.replace(
    "_runtimeContext.PublishGeneratedCodecs(candidate.Codecs);",
    "_runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);"
)
Path(server).write_text(text)
replace_once(server, r'''                        _dynamicModules.Add(newAssembly, newModule);
                        _detachedModuleServices.Add(oldModule, detachedServices);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
''', r'''                        _runtimeContext.PublishGeneratedCodecs(candidate.Codecs, codecRegistration);
                        _dynamicModules.Add(newAssembly, newModule);
                        _detachedModuleServices.Add(oldModule, detachedServices);
                        _unregisterOperations.Add(oldAssembly, drainOperation);
                        _runtimeContext.AdoptGeneratedManifest(codecRegistration);
''')
replace_once(server, r'''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        var identity = ownerAssembly.FullName;
        if (identity is not null && EnumerateManifestDependencies(manifest)
            .Any(dependency => string.Equals(dependency, identity, StringComparison.Ordinal)))
        {
            return true;
        }

        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }

        return referencedDependencies.Any(dependency =>
            dependency is not null &&
            dependency.TargetType is { } targetType &&
            ReferenceEquals(targetType.Assembly, ownerAssembly));
    }
''', r'''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        foreach (var dependency in EnumerateManifestDependencies(manifest))
        {
            if (SharpLinkGeneratedDependencyBinding.Matches(
                    manifest.OwnerAssembly,
                    dependency,
                    ownerAssembly))
            {
                return true;
            }
        }

        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }

        return referencedDependencies.Any(dependency =>
            dependency is not null &&
            dependency.TargetType is { } targetType &&
            ReferenceEquals(targetType.Assembly, ownerAssembly));
    }
''')
replace_once(server, r'''    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly.FullName ?? string.Empty);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly.FullName ?? string.Empty);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(dependency, self, StringComparison.Ordinal) || available.Contains(dependency))
                continue;
            return CreateError(
                SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must be registered and running before '{self}'.",
                incoming.OwnerAssembly,
                artifact: "Dependency");
        }
        return null;
    }
''', r'''    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            var boundAssembly = SharpLinkGeneratedDependencyBinding.Resolve(
                incoming.OwnerAssembly,
                dependency);
            if (ReferenceEquals(boundAssembly, incoming.OwnerAssembly) ||
                boundAssembly is not null && available.Contains(boundAssembly))
            {
                continue;
            }
            return CreateError(
                SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must resolve through '{self}' to the exact registered and running Assembly generation before registration.",
                incoming.OwnerAssembly,
                artifact: "Dependency");
        }
        return null;
    }
''')

# Maintainability: move referenced dependency RuntimeContext coverage to a dedicated partial file.
unit = "test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextTests.cs"
replace_once(unit, "public class SharpLinkRuntimeContextTests\n", "public partial class SharpLinkRuntimeContextTests\n")
remove_once(unit, r'''    [Test]
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

''')
remove_once(unit, r'''    private sealed class ReferencedCodecManifest(
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

''')
Path("test/SharpLink.UnitTests/Runtime/SharpLinkRuntimeContextReferencedCodecTests.cs").write_text(r'''using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    [Test]
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
    public void PendingManifestShouldBeValidatedAgainstFinalCandidateSnapshot()
    {
        var expectedHash = new RpcHash128(0x1111111111111111UL, 0x2222222222222222UL);
        var provider = new TestManifest(
            "referenced-provider",
            new HashedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(1), expectedHash));
        using var context = CreateRuntimeBuilder().Build(
            new ISharpLinkGeneratedAssemblyManifest[] { provider });
        var pending = context.PrepareGeneratedManifest(new ReferencedCodecManifest(
            "pending-consumer",
            [new SharpLinkReferencedCodecDependency(typeof(ThirdAdapterValue), expectedHash)]));
        try
        {
            var failure = CaptureFailure(() => context.PublishGeneratedCodecs(
                new Dictionary<Type, RpcGeneratedCodecRegistration>(), pending));

            Ensure(failure is InvalidOperationException &&
                   failure.Message.Contains("no generated Codec is registered for that exact Type", StringComparison.Ordinal),
                "an incoming not-yet-adopted manifest must be checked against the final candidate snapshot");
        }
        finally
        {
            pending.Dispose();
        }
    }

    private sealed class ReferencedCodecManifest(
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
}
''')

# Integration maintainability: extract dependency-identity scenarios into their own partial file.
integration = "test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs"
replace_once(integration, "public sealed class RuntimeAssemblyIntegrationTests\n", "public sealed partial class RuntimeAssemblyIntegrationTests\n")
remove_once(integration, r'''    [Test]
    [NotInParallel]
    public async Task SameFullNameReferencedCodecDependencyShouldRequireExactGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetReferencedCodecOutputDirectory();
        var firstContext = new PluginLoadContext("referenced-codec-generation-1", directory);
        var secondContext = new PluginLoadContext("referenced-codec-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "test setup must load two distinct provider generations with the same Assembly.FullName");
            var consumerManifestType = consumer2.GetType(
                "SharpLink.ReferencedCodecConsumer.ConsumerManifest",
                throwOnError: true)!;
            var consumerManifest = (ISharpLinkReferencedCodecDependencyManifest)Activator.CreateInstance(
                consumerManifestType)!;
            var typedDependency = consumerManifest.ReferencedCodecDependencies.Single();
            Ensure(ReferenceEquals(typedDependency.TargetType.Assembly, provider2),
                "consumer generation 2 must retain the exact provider generation selected by its runtime Type binding");

            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers generation-1 provider");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers generation-1 provider");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongClient.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"client must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongServer.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"server must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases generation-1 provider after rejected consumer");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases generation-1 provider after rejected consumer");

            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact generation-2 provider");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact generation-2 provider");
            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts consumer with exact bound provider generation and expected CodecHash");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts consumer with exact bound provider generation and expected CodecHash");

            try
            {
                _ = await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: client must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "client reverse dependency check uses exact provider Assembly generation");
            }
            try
            {
                _ = await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: server must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "server reverse dependency check uses exact provider Assembly generation");
            }

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases typed consumer before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases typed consumer before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact provider after dependant removal");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact provider after dependant removal");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

''')
remove_once(integration, r'''    private static string GetReferencedCodecOutputDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
        return Path.Combine(
            directory.FullName,
            "test",
            "SharpLink.ReferencedCodecConsumer",
            "bin",
            "Release",
            "net10.0");
    }

''')

Path("test/SharpLink.IntegrationTests/RuntimeAssemblyDependencyIdentityIntegrationTests.cs").write_text(r'''using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task SameFullNameReferencedCodecDependencyShouldRequireExactGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ReferencedCodecConsumer");
        var firstContext = new PluginLoadContext("referenced-codec-generation-1", directory);
        var secondContext = new PluginLoadContext("referenced-codec-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "test setup must load two distinct provider generations with the same Assembly.FullName");
            var consumerManifestType = consumer2.GetType(
                "SharpLink.ReferencedCodecConsumer.ConsumerManifest",
                throwOnError: true)!;
            var consumerManifest = (ISharpLinkReferencedCodecDependencyManifest)Activator.CreateInstance(
                consumerManifestType)!;
            var typedDependency = consumerManifest.ReferencedCodecDependencies.Single();
            Ensure(ReferenceEquals(typedDependency.TargetType.Assembly, provider2),
                "consumer generation 2 must retain the exact provider generation selected by its runtime Type binding");

            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers generation-1 provider");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers generation-1 provider");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongClient.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"client must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongServer.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"server must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases generation-1 provider after rejected consumer");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases generation-1 provider after rejected consumer");

            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact generation-2 provider");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact generation-2 provider");

            var clientReplacement = await harness.Client.ReplaceAssemblyAsync(
                provider2, consumer2, TimeSpan.FromSeconds(2));
            Ensure(!clientReplacement.Succeeded &&
                   clientReplacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   clientReplacement.Error.Message.Contains("exact Type", StringComparison.Ordinal),
                $"client replacement must validate the pending consumer against the final candidate snapshot: {clientReplacement.Error}");
            var serverReplacement = await harness.Server.ReplaceAssemblyAsync(
                provider2, consumer2, TimeSpan.FromSeconds(2));
            Ensure(!serverReplacement.Succeeded &&
                   serverReplacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   serverReplacement.Error.Message.Contains("exact Type", StringComparison.Ordinal),
                $"server replacement must validate the pending consumer against the final candidate snapshot: {serverReplacement.Error}");

            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts consumer with exact bound provider generation and expected CodecHash");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts consumer with exact bound provider generation and expected CodecHash");

            var clientCodec = ResolveManifestCodec(harness.Client, consumer2, typedDependency.TargetType);
            var serverCodec = ResolveManifestCodec(harness.Server, consumer2, typedDependency.TargetType);
            Ensure(ReferenceEquals(clientCodec.GetType().Assembly, provider2),
                "client contract provider resolves the exact referenced generated Codec rather than falling back");
            Ensure(ReferenceEquals(serverCodec.GetType().Assembly, provider2),
                "server contract provider resolves the exact referenced generated Codec rather than falling back");

            try
            {
                _ = await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: client must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "client reverse dependency check uses exact provider Assembly generation");
            }
            try
            {
                _ = await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: server must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "server reverse dependency check uses exact provider Assembly generation");
            }

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases typed consumer before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases typed consumer before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact provider after dependant removal");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact provider after dependant removal");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

    [Test]
    [NotInParallel]
    public async Task SameFullNameDeclaredModuleDependencyShouldRequireExactBoundGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ModuleDependencyConsumer");
        var firstContext = new PluginLoadContext("module-dependency-generation-1", directory);
        var secondContext = new PluginLoadContext("module-dependency-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ModuleDependencyConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "module dependency setup must load distinct same-FullName provider generations");
            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers only the wrong provider generation");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers only the wrong provider generation");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"client must not satisfy a CLR-bound module dependency with another same-FullName generation: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"server must not satisfy a CLR-bound module dependency with another same-FullName generation: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client removes wrong provider generation");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server removes wrong provider generation");
            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact bound provider generation");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact bound provider generation");
            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts ordinary module dependency with exact bound provider generation");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts ordinary module dependency with exact bound provider generation");

            await EnsureDependencyPreventsUnregisterAsync(
                () => harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2)),
                "client ordinary module dependency reverse check");
            await EnsureDependencyPreventsUnregisterAsync(
                () => harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2)),
                "server ordinary module dependency reverse check");

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases ordinary dependant before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases ordinary dependant before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact ordinary dependency provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact ordinary dependency provider");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

    private static async Task EnsureDependencyPreventsUnregisterAsync(
        Func<ValueTask<SharpLinkAssemblyUnregisterResult>> unregister,
        string message)
    {
        try
        {
            _ = await unregister();
            throw new Exception($"assert failed: {message}");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal), message);
        }
    }

    private static object ResolveManifestCodec(object endpoint, Assembly ownerAssembly, Type targetType)
    {
        var runtimeContext = GetEndpointRuntimeContext(endpoint);
        var provider = RpcGeneratedCodecResolver.GetProvider(runtimeContext, ownerAssembly);
        var method = typeof(IRpcCodecProvider).GetMethod(nameof(IRpcCodecProvider.GetCodec))
            ?? throw new MissingMethodException(nameof(IRpcCodecProvider), nameof(IRpcCodecProvider.GetCodec));
        return method.MakeGenericMethod(targetType).Invoke(provider, null)
            ?? throw new InvalidOperationException($"Codec resolution for '{targetType}' returned null.");
    }

    private static IRpcRuntimeContext GetEndpointRuntimeContext(object endpoint)
    {
        if (endpoint is IRpcChannel channel)
            return channel.RuntimeContext;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return endpoint.GetType().GetField("_runtimeContext", flags)?.GetValue(endpoint) as IRpcRuntimeContext
            ?? throw new InvalidOperationException($"Runtime context was not available from '{endpoint.GetType()}'.");
    }

    private static string GetProjectOutputDirectory(string projectName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
        return Path.Combine(
            directory.FullName,
            "test",
            projectName,
            "bin",
            "Release",
            "net10.0");
    }
}
''')

# A hand-authored manifest with only legacy-shaped string module dependency, but a real CLR AssemblyRef.
Path("test/SharpLink.ModuleDependencyConsumer/SharpLink.ModuleDependencyConsumer.csproj").parent.mkdir(parents=True, exist_ok=True)
Path("test/SharpLink.ModuleDependencyConsumer/SharpLink.ModuleDependencyConsumer.csproj").write_text(r'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SharpLink.Abstractions\SharpLink.Abstractions.csproj" />
    <ProjectReference Include="..\SharpLink.ReferencedCodecProvider\SharpLink.ReferencedCodecProvider.csproj" />
  </ItemGroup>
</Project>
''')
Path("test/SharpLink.ModuleDependencyConsumer/ModuleDependencyConsumer.cs").write_text(r'''using SharpLink.Abstractions;
using SharpLink.ReferencedCodecProvider;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ModuleDependencyConsumer.ModuleDependencyManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ModuleDependencyConsumer;

public sealed class ModuleDependencyManifest : ISharpLinkGeneratedAssemblyManifest
{
    private static readonly IReadOnlyList<string> ModuleDependencies =
        new[] { typeof(Payload).Assembly.FullName! };

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ModuleDependencyManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x4d6f64756c654465UL, 0x70656e64656e6379UL);
    public string CompileTimeDescriptor => "module-dependency-consumer";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => ModuleDependencies;
}
''')

csproj = "test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj"
replace_once(csproj, r'''    <ProjectReference Include="..\SharpLink.ReferencedCodecConsumer\SharpLink.ReferencedCodecConsumer.csproj" ReferenceOutputAssembly="false" />
''', r'''    <ProjectReference Include="..\SharpLink.ReferencedCodecConsumer\SharpLink.ReferencedCodecConsumer.csproj" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\SharpLink.ModuleDependencyConsumer\SharpLink.ModuleDependencyConsumer.csproj" ReferenceOutputAssembly="false" />
''')
