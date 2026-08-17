using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public sealed class ManifestSourceIsolationTests
{
    [Test]
    public void RuntimeCompileShouldCaptureItsSourceExactlyOnceAndFreezeTheReturnedList()
    {
        var mutableManifests = new List<ISharpLinkGeneratedAssemblyManifest>
        {
            CodecManifest.For<CodecValueA>("source-a")
        };
        var source = new CountingManifestSource(() => mutableManifests);
        var plan = new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(source)
            .Compile();

        mutableManifests.Clear();
        Ensure(source.CreateSnapshotCount == 1,
            "Compile must query its configured ManifestSource exactly once");
        Ensure(plan.GeneratedManifests.Count == 1,
            "the plan must own a defensive point-in-time manifest snapshot");

        using var context = plan.Materialize();
        Ensure(context.Codecs.GetCodec<CodecValueA>() is TestCodec<CodecValueA>,
            "materialization must consume the plan snapshot without querying the source again");
        Ensure(source.CreateSnapshotCount == 1,
            "materialization must not retain or re-query the ManifestSource");
    }

    [Test]
    // The poison entry mutates the process-wide weak catalog. Explicit sources must ignore it, but any
    // default-source consumer (a Builder without UseGeneratedManifestSource) snapshots that same catalog
    // during Build, so the poison window must be exclusive against the entire suite: keyless
    // [NotInParallel] runs completely alone in TUnit. A keyed constraint would still race unconstrained
    // tests — the issue #228 phase15-global-poison flake.
    [NotInParallel]
    public async Task ClientAndServerBuildShouldShareTheirPlanSnapshotWithoutReadingTheGlobalCatalog()
    {
        var poison = new IncompatibleCatalogPoisonManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(poison);
        var clientManifest = CompositeManifest.ForClient<CodecValueA>(
            typeof(IContractA),
            8_301,
            static channel => new ContractAProxy(channel));
        var serverStubFactoryCount = 0;
        var serverManifest = CompositeManifest.ForServer<CodecValueB>(
            typeof(IContractB),
            typeof(ContractBService),
            8_304,
            provider =>
            {
                Ensure(provider.GetCodec<CodecValueB>() is TestCodec<CodecValueB>,
                    "the Server service plan and Runtime must consume the same combined manifest snapshot");
                Interlocked.Increment(ref serverStubFactoryCount);
                return new TestStub(8_304);
            });
        var clientSource = new CountingManifestSource(
            [clientManifest]);
        var serverSource = new CountingManifestSource([serverManifest]);
        var clientTransport = new TrackingClientTransport();
        var serverListener = new TrackingServerListener();
        try
        {
            await using var client = SharpClientBuilder.Create()
                .UseGeneratedManifestSource(clientSource)
                .UseTransport(clientTransport)
                .Build();
            await using var server = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(serverSource)
                .UseTransport(serverListener)
                .Build();

            Ensure(clientSource.CreateSnapshotCount == 1 && serverSource.CreateSnapshotCount == 1,
                "Client and Server Compile must each query only their own source once");
            Ensure(client.Get<IContractA>() is ContractAProxy &&
                   ((IRpcChannel)client).RuntimeContext.Codecs.GetCodec<CodecValueA>() is TestCodec<CodecValueA>,
                "the Client facade and Runtime must both materialize the same combined frozen snapshot");
            Ensure(serverStubFactoryCount == 1,
                "the Server service plan must materialize once from the same snapshot as its Runtime Codec");

            var planBuilder = SharpClientBuilder.Create()
                .UseTransport(new TrackingClientTransport());
            var plan = planBuilder.CompileForMultiCluster([clientManifest]);
            var clientPlanSnapshot = plan.RuntimeContext.GeneratedManifests;
            await using (var plannedClient = (SharpLinkClient)planBuilder.MaterializeCompiledPlan(plan))
                Ensure(ReferenceEquals(
                        clientPlanSnapshot,
                        GetFinalManifestSnapshot(plannedClient)),
                    "the materialized Client must retain the Runtime plan's exact frozen snapshot object");

            var serverPlanBuilder = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(new FixedGeneratedManifestSource(
                    [CodecManifest.For<CodecValueB>("server-plan-identity")]))
                .UseTransport(new TrackingServerListener());
            var serverPlan = CompileServerPlan(serverPlanBuilder);
            var serverPlanSnapshot = serverPlan.RuntimeContext.GeneratedManifests;
            await using (var plannedServer = MaterializeServerPlan(serverPlanBuilder, serverPlan))
                Ensure(ReferenceEquals(
                        serverPlanSnapshot,
                        GetFinalManifestSnapshot(plannedServer)),
                    "the materialized Server must retain the Runtime plan's exact frozen snapshot object");

            await client.StopAsync();
            await server.StopAsync(TimeSpan.Zero);
            Ensure(clientSource.CreateSnapshotCount == 1 && serverSource.CreateSnapshotCount == 1,
                "Client/Server Stop must not query bootstrap discovery");
        }
        finally
        {
            _ = RollbackTestIsolation.RemoveManifestFromCatalog(poison);
        }
    }

    [Test]
    public async Task ThirtyTwoParallelContextsShouldKeepManifestTimeAndDisposalOwnershipIsolated()
    {
        const int contextsPerSource = 16;
        var disposalA = new DisposableScopeCounters();
        var disposalB = new DisposableScopeCounters();
        var sourceA = new CountingManifestSource(
            [CodecManifest.ForDisposableScope<CodecValueA>("parallel-a", disposalA)]);
        var sourceB = new CountingManifestSource(
            [CodecManifest.ForDisposableScope<CodecValueB>("parallel-b", disposalB)]);
        var timeA = new ManualTimeProvider(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var timeB = new ManualTimeProvider(new DateTimeOffset(2041, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var builds = Enumerable.Range(0, contextsPerSource * 2)
            .Select(index => Task.Run(() => new SharpLinkRuntimeContextBuilder()
                .UseGeneratedManifestSource(index % 2 == 0 ? sourceA : sourceB)
                .UseTimeProvider(index % 2 == 0 ? timeA : timeB)
                .Build()))
            .ToArray();
        var contexts = await Task.WhenAll(builds);

        Ensure(sourceA.CreateSnapshotCount == contextsPerSource &&
               sourceB.CreateSnapshotCount == contextsPerSource,
            "every parallel Compile must take exactly one independent source snapshot");
        Ensure(disposalA.ScopeCreateCount == contextsPerSource &&
               disposalB.ScopeCreateCount == contextsPerSource,
            "every Runtime must create its own manifest-owned adapter scope");
        for (var index = 0; index < contexts.Length; index++)
        {
            var context = contexts[index];
            if (index % 2 == 0)
            {
                Ensure(ReferenceEquals(context.TimeProvider, timeA) &&
                       context.Codecs.GetCodec<CodecValueA>() is TestCodec<CodecValueA>,
                    $"context {index} must retain only source/time A");
                EnsureCodecIsMissing<CodecValueB>(context);
            }
            else
            {
                Ensure(ReferenceEquals(context.TimeProvider, timeB) &&
                       context.Codecs.GetCodec<CodecValueB>() is TestCodec<CodecValueB>,
                    $"context {index} must retain only source/time B");
                EnsureCodecIsMissing<CodecValueA>(context);
            }
        }

        var timeBBefore = timeB.GetUtcNow();
        timeA.Advance(TimeSpan.FromHours(7));
        Ensure(timeB.GetUtcNow() == timeBBefore,
            "advancing one instance TimeProvider must not change another instance");

        for (var index = 0; index < contexts.Length; index += 2)
            contexts[index].Dispose();
        Ensure(disposalA.ScopeDisposeCount == contextsPerSource && disposalB.ScopeDisposeCount == 0,
            "disposing all source-A contexts must release only their own adapter scopes");
        for (var index = 1; index < contexts.Length; index += 2)
        {
            Ensure(contexts[index].Codecs.GetCodec<CodecValueB>() is TestCodec<CodecValueB>,
                "disposing every source-A runtime must not invalidate a source-B runtime");
            contexts[index].Dispose();
        }
        Ensure(disposalB.ScopeDisposeCount == contextsPerSource,
            "each source-B context must release its own adapter scope exactly once");
    }

    [Test]
    public async Task EqualContractIdsShouldConflictOnlyWhenTheyShareOneFrozenSnapshot()
    {
        var sourceA = new CountingManifestSource(
            [ContractManifest.For<IContractA>(8_302, static channel => new ContractAProxy(channel))]);
        var sourceB = new CountingManifestSource(
            [ContractManifest.For<IContractB>(8_302, static channel => new ContractBProxy(channel))]);

        await using var clientA = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(sourceA)
            .UseTransport(new TrackingClientTransport())
            .Build();
        await using var clientB = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(sourceB)
            .UseTransport(new TrackingClientTransport())
            .Build();
        Ensure(clientA.Get<IContractA>() is ContractAProxy && clientB.Get<IContractB>() is ContractBProxy,
            "equal IDs in independent snapshots must not create process-global conflicts");

        var conflictingTransport = new TrackingClientTransport();
        var conflictingSource = new CountingManifestSource(
        [
            ContractManifest.For<IContractA>(8_302, static channel => new ContractAProxy(channel)),
            ContractManifest.For<IContractB>(8_302, static channel => new ContractBProxy(channel))
        ]);
        var failure = Capture(() => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(conflictingSource)
            .UseTransport(conflictingTransport)
            .Build());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("Contract conflict", StringComparison.Ordinal),
            "equal IDs inside one frozen Client snapshot must fail deterministically");
        Ensure(conflictingSource.CreateSnapshotCount == 1 && conflictingTransport.DisposeCount == 1,
            "conflict validation must consume one snapshot and roll back the unbuilt transport once");
    }

    [Test]
    public void DynamicRegistrationInOneRuntimeShouldNotMutateItsPeerSnapshot()
    {
        using var contextA = new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource(
                [CodecManifest.For<CodecValueA>("dynamic-owner-a")]))
            .Build();
        using var contextB = new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource(
                [CodecManifest.For<CodecValueB>("dynamic-peer-b")]))
            .Build();
        var originalA = contextA.CreateGeneratedCodecSnapshot();
        var registration = contextA.PrepareGeneratedManifest(
            CodecManifest.For<DynamicCodecValue>("dynamic-add"));
        var withDynamic = originalA
            .Concat(registration.Codecs)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        contextA.AdoptGeneratedManifest(registration);
        contextA.PublishGeneratedCodecs(withDynamic);
        Ensure(contextA.Codecs.GetCodec<DynamicCodecValue>() is TestCodec<DynamicCodecValue>,
            "the owning runtime must publish its explicit dynamic registration");
        EnsureCodecIsMissing<DynamicCodecValue>(contextB);

        contextA.PublishGeneratedCodecs(originalA);
        contextA.ReleaseGeneratedManifest(registration);
        EnsureCodecIsMissing<DynamicCodecValue>(contextA);
        Ensure(contextB.Codecs.GetCodec<CodecValueB>() is TestCodec<CodecValueB>,
            "owner unregister must not change the peer's initial snapshot");
    }

    [Test]
    public void BuilderPreconditionsShouldFailBeforeQueryingAConfiguredSource()
    {
        var clientSource = new CountingManifestSource(
            static () => throw new InvalidOperationException("client source must not run"));
        var serverSource = new CountingManifestSource(
            static () => throw new InvalidOperationException("server source must not run"));

        var clientFailure = Capture(() => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(clientSource)
            .Build());
        var serverFailure = Capture(() => SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(serverSource)
            .Build());

        Ensure(clientFailure is InvalidOperationException clientException &&
               clientException.Message.Contains("Transport", StringComparison.Ordinal) &&
               clientSource.CreateSnapshotCount == 0,
            "Client topology validation must precede source capture");
        Ensure(serverFailure is InvalidOperationException serverException &&
               serverException.Message.Contains("Transport", StringComparison.Ordinal) &&
               serverSource.CreateSnapshotCount == 0,
            "Server transport validation must precede source capture");
    }

    [Test]
    public async Task ParallelClientServerStopShouldNotReenterBootstrapDiscovery()
    {
        const int ownerCount = 8;
        var source = new CountingManifestSource([]);
        var clientTransports = Enumerable.Range(0, ownerCount)
            .Select(static _ => new TrackingClientTransport())
            .ToArray();
        var serverListeners = Enumerable.Range(0, ownerCount)
            .Select(static _ => new TrackingServerListener())
            .ToArray();
        var clients = await Task.WhenAll(clientTransports.Select(transport => Task.Run(() =>
            SharpClientBuilder.Create()
                .UseGeneratedManifestSource(source)
                .UseTransport(transport)
                .Build())));
        var servers = await Task.WhenAll(serverListeners.Select(listener => Task.Run(() =>
            SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(source)
                .UseTransport(listener)
                .Build())));

        Ensure(source.CreateSnapshotCount == ownerCount * 2,
            "each parallel Client/Server Compile must capture once");
        await Task.WhenAll(
            clients.Select(static client => client.StopAsync().AsTask())
                .Concat(servers.Select(static server => server.StopAsync(TimeSpan.Zero).AsTask())));
        Ensure(source.CreateSnapshotCount == ownerCount * 2,
            "parallel Stop/Dispose must consume only instance state");
        Ensure(clientTransports.All(static transport => transport.DisposeCount == 1) &&
               serverListeners.All(static listener => listener.DisposeCount == 1),
            "every parallel owner must release its transport exactly once");
    }

    [Test]
    // The collectible rollback plugin uses process-wide module state while its real Codec owner unloads.
    [NotInParallel("rollback-plugin")]
    public async Task DisposedIsolatedSnapshotRuntimeShouldReleaseCollectibleManifestAndCodecOwners()
    {
        await RollbackState.TestIsolation.WaitAsync();
        CollectibleRuntimeReferences references;
        try
        {
            references = CreateAndDisposeCollectibleSnapshotRuntime();
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }

        for (var attempt = 0;
             attempt < 12 && (references.Manifest.IsAlive || references.LoadContext.IsAlive);
             attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Ensure(!references.Manifest.IsAlive && !references.LoadContext.IsAlive,
            "a disposed but still-live Runtime must release collectible manifest, Codec, and ALC owners");
        GC.KeepAlive(references.Context);
    }

    private static void EnsureCodecIsMissing<T>(SharpLinkRuntimeContext context)
    {
        var failure = Capture(() => _ = context.Codecs.GetCodec<T>());
        Ensure(failure is NotSupportedException,
            $"runtime must not resolve unregistered Codec '{typeof(T).Name}'");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CollectibleRuntimeReferences CreateAndDisposeCollectibleSnapshotRuntime()
    {
        var loadContext = new ManifestPluginLoadContext($"phase15-manifest-{Guid.NewGuid():N}");
        var loadContextReference = new WeakReference(loadContext);
        var assembly = loadContext.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location);
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(
            assembly.GetType(typeof(RollbackManifest).FullName!, throwOnError: true)!)!;
        var manifestReference = new WeakReference(manifest);
        var source = new FixedGeneratedManifestSource([manifest]);
        var context = new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(source)
            .Build();
        try
        {
            _ = context.Codecs.GetCodec<string>();
        }
        finally
        {
            try
            {
                context.Dispose();
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("rollback Adapter scope cleanup failed", StringComparison.Ordinal),
                    "the collectible test plugin must execute its real generated Codec cleanup path");
            }
        }

        source = null!;
        manifest = null!;
        assembly = null!;
        loadContext.Unload();
        loadContext = null!;
        return new CollectibleRuntimeReferences(context, manifestReference, loadContextReference);
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> GetFinalManifestSnapshot(object owner)
        => (IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>)(owner.GetType().GetField(
                "_staticManifests",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) ?? throw new Exception("materialized runtime has no frozen manifest snapshot"));

    private static ServerBuildPlan CompileServerPlan(SharpLinkServerBuilder builder)
        => (ServerBuildPlan)(typeof(SharpLinkServerBuilder).GetMethod(
                "CompileForBuild",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(builder, null) ?? throw new Exception("Server Builder Compile seam was not found"));

    private static SharpLinkServer MaterializeServerPlan(
        SharpLinkServerBuilder builder,
        ServerBuildPlan plan)
        => (SharpLinkServer)(typeof(SharpLinkServerBuilder).GetMethod(
                "Materialize",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(ServerBuildPlan)],
                modifiers: null)
            ?.Invoke(builder, [plan]) ?? throw new Exception("Server Builder materialization seam was not found"));

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class CountingManifestSource : IGeneratedManifestSource
    {
        private readonly Func<IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>> _createSnapshot;
        private int _createSnapshotCount;

        internal CountingManifestSource(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
            : this(() => manifests)
        {
        }

        internal CountingManifestSource(
            Func<IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>> createSnapshot)
            => _createSnapshot = createSnapshot;

        internal int CreateSnapshotCount => Volatile.Read(ref _createSnapshotCount);

        public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
        {
            Interlocked.Increment(ref _createSnapshotCount);
            return _createSnapshot();
        }
    }

    private sealed class CodecManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private CodecManifest(string descriptor, IRpcGeneratedCodecFactory factory)
        {
            CompileTimeDescriptor = descriptor;
            Codecs = [factory];
        }

        internal static CodecManifest For<T>(string descriptor)
            => new(descriptor, new TestCodecFactory<T>(descriptor));

        internal static CodecManifest ForDisposableScope<T>(
            string descriptor,
            DisposableScopeCounters counters)
            => new(descriptor, new DisposableScopeCodecFactory<T>(descriptor, counters));

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase15-test";
        public Assembly OwnerAssembly => typeof(ManifestSourceIsolationTests).Assembly;
        public string CompileTimeDescriptor { get; }
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ContractManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private ContractManifest(SharpLinkGeneratedContractDescriptor contract)
            => Contracts = [contract];

        internal static ContractManifest For<TContract>(
            long contractId,
            Func<IRpcChannel, TContract> proxyFactory)
            where TContract : IService
            => new(new SharpLinkGeneratedContractDescriptor(
                typeof(TContract),
                typeof(TContract).FullName!,
                contractId,
                new string('a', 64),
                [],
                channel => proxyFactory(channel),
                static _ => throw new NotSupportedException()));

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase15-test";
        public Assembly OwnerAssembly => typeof(ManifestSourceIsolationTests).Assembly;
        public string CompileTimeDescriptor => "phase15-contract";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class CompositeManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private CompositeManifest(
            SharpLinkGeneratedContractDescriptor contract,
            SharpLinkGeneratedServiceDescriptor? service,
            IRpcGeneratedCodecFactory codec)
        {
            Contracts = [contract];
            Services = service is null ? [] : [service];
            Codecs = [codec];
        }

        internal static CompositeManifest ForClient<TCodec>(
            Type contractType,
            long contractId,
            Func<IRpcChannel, object> proxyFactory)
            => new(
                CreateContract(
                    contractType,
                    contractId,
                    proxyFactory,
                    static _ => new TestStub(8_301)),
                service: null,
                new TestCodecFactory<TCodec>($"client-composite:{typeof(TCodec).FullName}"));

        internal static CompositeManifest ForServer<TCodec>(
            Type contractType,
            Type implementationType,
            long contractId,
            Func<IRpcCodecProvider, IRpcStub> stubFactory)
        {
            var contract = CreateContract(
                contractType,
                contractId,
                static _ => throw new NotSupportedException(),
                stubFactory);
            var service = new SharpLinkGeneratedServiceDescriptor(
                contractType,
                implementationType,
                contractType.FullName!,
                implementationType.FullName!,
                contractId,
                contract.Fingerprint,
                SharpLinkServiceLifetime.Call,
                [],
                _ => Activator.CreateInstance(implementationType)!);
            return new CompositeManifest(
                contract,
                service,
                new TestCodecFactory<TCodec>($"server-composite:{typeof(TCodec).FullName}"));
        }

        private static SharpLinkGeneratedContractDescriptor CreateContract(
            Type contractType,
            long contractId,
            Func<IRpcChannel, object> proxyFactory,
            Func<IRpcCodecProvider, IRpcStub> stubFactory)
            => new(
                contractType,
                contractType.FullName!,
                contractId,
                new string('c', 64),
                [],
                proxyFactory,
                stubFactory);

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase15-test";
        public Assembly OwnerAssembly => typeof(ManifestSourceIsolationTests).Assembly;
        public string CompileTimeDescriptor => "phase15-composite";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; }
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class IncompatibleCatalogPoisonManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api + 1;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase15-global-poison";
        public Assembly OwnerAssembly => typeof(ManifestSourceIsolationTests).Assembly;
        public string CompileTimeDescriptor => throw new InvalidOperationException("poison shape read");
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts =>
            throw new InvalidOperationException("poison shape read");
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services =>
            throw new InvalidOperationException("poison shape read");
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs =>
            throw new InvalidOperationException("poison shape read");
        public IReadOnlyList<string> Dependencies => throw new InvalidOperationException("poison shape read");
    }

    private sealed class TestCodecFactory<T>(string schemaId) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public string SchemaId { get; } = schemaId;
        public string WireFormatId => "sharplink-native/v1";
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new TestCodec<T>()
                : throw new ArgumentException("Native Codec does not accept an adapter scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class TestCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
        {
        }

        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class DisposableScopeCodecFactory<T>(
        string schemaId,
        DisposableScopeCounters counters) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public string SchemaId { get; } = schemaId;
        public string WireFormatId => "phase15-disposable/v1";
        public string AdapterId => "phase15.disposable-scope/v1";
        public IRpcCodecAdapter Adapter { get; } = new DisposableScopeAdapter(counters);
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<T>();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class DisposableScopeAdapter(DisposableScopeCounters counters) : IRpcCodecAdapter
    {
        public string AdapterId => "phase15.disposable-scope/v1";
        public string WireFormatId => "phase15-disposable/v1";
        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref counters.ScopeCreateCount);
            return new DisposableScope(counters);
        }
    }

    private sealed class DisposableScope(DisposableScopeCounters counters) : IRpcCodecAdapterScope
    {
        private int _disposed;

        public IRpcCodec<T> CreateCodec<T>()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new TestCodec<T>();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref counters.ScopeDisposeCount);
        }
    }

    private sealed class DisposableScopeCounters
    {
        internal int ScopeCreateCount;
        internal int ScopeDisposeCount;
    }

    private sealed class TestStub(long interfaceHash) : IRpcStub
    {
        public long InterfaceHash { get; } = interfaceHash;
        public ValueTask InvokeNoReturnAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output) => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(
            object service,
            IRpcGeneratedServerBridge bridge,
            long methodHash,
            long requestId,
            ReadOnlySequence<byte> args,
            IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TrackingClientTransport : IClientTransportFactory
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingServerListener : IServerTransportListener
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManifestPluginLoadContext(string name)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private readonly record struct CollectibleRuntimeReferences(
        SharpLinkRuntimeContext Context,
        WeakReference Manifest,
        WeakReference LoadContext);

    private interface IContractA : IService;
    private interface IContractB : IService;
    private sealed class ContractAProxy(IRpcChannel channel) : IContractA
    {
        internal IRpcChannel Channel { get; } = channel;
    }
    private sealed class ContractBProxy(IRpcChannel channel) : IContractB
    {
        internal IRpcChannel Channel { get; } = channel;
    }
    private sealed class ContractBService : IContractB;
    private sealed class CodecValueA;
    private sealed class CodecValueB;
    private sealed class DynamicCodecValue;
}
