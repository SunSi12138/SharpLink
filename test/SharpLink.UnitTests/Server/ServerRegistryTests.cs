using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerRegistryTests
{
    [Test]
    public void SharpLinkServerShouldOwnFocusedRegistryCollaboratorsInsteadOfRegistryStateFields()
    {
        var fields = typeof(SharpLinkServer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);

        Ensure(fields.Any(static field => field.Name == "_connectionRegistry" &&
                                          field.FieldType == typeof(ServerConnectionRegistry)),
            "SharpLinkServer must compose the focused connection registry");
        Ensure(fields.Any(static field => field.Name == "_serviceModuleRegistry" &&
                                          field.FieldType == typeof(ServerServiceModuleRegistry)),
            "SharpLinkServer must compose the focused service/module registry");

        var extractedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "_connections",
            "_retiredConnections",
            "_services",
            "_registryGate",
            "_dynamicModules",
            "_unregisterOperations",
            "_detachedModuleServices",
            "_registryGeneration"
        };
        Ensure(fields.All(field => !extractedFields.Contains(field.Name)),
            "SharpLinkServer must not retain mutable registry state fields after extraction");
    }

    [Test]
    public async Task ConnectionRegistryShouldProtectReplacementAndRetiredCleanupOwnership()
    {
        var registry = new ServerConnectionRegistry();
        var first = CreateState();
        var replacement = CreateState();
        Ensure(first.MarkReady(null), "first connection ready");
        Ensure(replacement.MarkReady(null), "replacement connection ready");
        const string id = "registry-connection";

        try
        {
            Ensure(registry.TryAdd(id, first), "first connection must publish");
            Ensure(registry.TryUpdate(id, replacement, first),
                "replacement must compare against the expected current connection");
            Ensure(!registry.TryRemove(new KeyValuePair<string, ServerConnectionState>(id, first)),
                "stale cleanup must not remove a newer connection with the same id");
            Ensure(registry.TryGetValue(id, out var current) && ReferenceEquals(current, replacement),
                "replacement must remain the current connection after stale cleanup");

            Ensure(registry.TryRetire(first), "retired ownership must publish exactly once");
            Ensure(!registry.TryRetire(first), "duplicate retirement must not create duplicate ownership");
            var owned = registry.SnapshotOwned();
            Ensure(owned.Length == 2 && owned.Contains(first) && owned.Contains(replacement),
                "owned snapshot must include both current and retired connections exactly once");

            await first.CloseAsync();
            await first.ServiceCleanupTask;
            Ensure(registry.CompleteRetired(first),
                "retired ownership must remain until service cleanup completes");
            Ensure(!registry.IsRetired(first), "completed retired cleanup must release registry ownership");
            Ensure(registry.TryRemove(new KeyValuePair<string, ServerConnectionState>(id, replacement)),
                "current connection must be removable by exact instance");
            Ensure(registry.SnapshotOwned().Length == 0,
                "registry must be empty after current and retired ownership are released");
        }
        finally
        {
            await first.CloseAsync();
            await replacement.CloseAsync();
            await first.ServiceCleanupTask;
            await replacement.ServiceCleanupTask;
        }
    }

    [Test]
    public void ServiceModuleRegistryShouldSnapshotAndReleaseCorrelatedOwnership()
    {
        var services = FrozenDictionary<long, ServiceRegistration>.Empty;
        var registry = new ServerServiceModuleRegistry(services);
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServerRegistryTests).Assembly);
        using var codecRegistration = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, codecRegistration);
        var unregisterOperation = Task.FromResult(new SharpLinkAssemblyUnregisterResult
        {
            ReferencesReleased = false
        });

        lock (registry.Gate)
        {
            registry.DynamicModules.Add(manifest.OwnerAssembly, module);
            registry.DetachedModuleServices.Add(module, []);
            registry.UnregisterOperations.Add(manifest.OwnerAssembly, unregisterOperation);
            registry.GenerationStorage++;
        }

        var published = registry.CaptureSnapshot();
        Ensure(ReferenceEquals(published.Services, services),
            "service snapshot must retain the published immutable table");
        Ensure(published.Generation == 1,
            "correlated registry mutation must advance its generation");
        Ensure(published.DynamicAssemblies is [var assembly] && ReferenceEquals(assembly, manifest.OwnerAssembly),
            "dynamic module snapshot must retain the exact Assembly identity");
        Ensure(published.UnregisterOperationCount == 1 && published.DetachedModuleServiceCount == 1,
            "in-flight unregister and detached-service ownership must be visible in one snapshot");

        lock (registry.Gate)
        {
            Ensure(registry.DetachedModuleServices.Remove(module, out var detached) && detached.Length == 0,
                "detached service ownership must transfer exactly once to cleanup");
            Ensure(!registry.DetachedModuleServices.Remove(module, out _),
                "detached service cleanup must not be observable twice");
            Ensure(registry.UnregisterOperations.Remove(manifest.OwnerAssembly),
                "completed unregister operation must leave the registry");
            Ensure(registry.DynamicModules.Remove(manifest.OwnerAssembly),
                "released dynamic module must leave the registry");
            registry.GenerationStorage++;
        }

        var released = registry.CaptureSnapshot();
        Ensure(released.Generation == 2 &&
               released.DynamicAssemblies.Length == 0 &&
               released.UnregisterOperationCount == 0 &&
               released.DetachedModuleServiceCount == 0,
            "cleanup must release all module-related registry ownership");
    }

    private static ServerConnectionState CreateState()
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            Guid.NewGuid().ToString("N"),
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
        return new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(RpcSessionTestFixture.RuntimeContext.Concurrency),
            CancellationToken.None,
            RpcSessionTestFixture.RuntimeContext.TimeProvider);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class EmptyManifest(Assembly ownerAssembly) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public RpcHash128 RpcAssemblyHash => new(0x7265676973747279UL, 0x2d746573742d7631UL);
        public string CompileTimeDescriptor => "registry-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
