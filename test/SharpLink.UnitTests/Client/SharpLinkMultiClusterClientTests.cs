using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterClientTests
{
    private static readonly Assembly TestManifestAssembly = CreateTestManifestAssembly();

    [Test]
    public async Task StaticRouteShouldCreateTheTargetChildProxyAndConnectEverySlot()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var ordersTransport = new TestClientTransportFactory();
        var paymentsTransport = new TestClientTransportFactory();

        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(ordersTransport))
            .AddCluster("payments", child => child.UseTransport(paymentsTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var proxy = client.Get<IOrdersContract>();
        Ensure(proxy is OrdersProxy, "Get should create the proxy directly from the routed child client");
        await client.ConnectAsync();

        Ensure(client.State == SharpLinkMultiClusterState.Ready, "all slots should be ready after shared connect");
        Ensure(ordersTransport.ConnectCount == 1, "orders child should connect once");
        Ensure(paymentsTransport.ConnectCount == 1, "payments child should connect once");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Ready, "orders slot state");
    }

    [Test]
    [NotInParallel]
    public async Task FilteredStaticRoutesShouldIgnoreUnrelatedGlobalManifests()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        ISharpLinkGeneratedAssemblyManifest? unrelatedManifest = new ThrowingCodecManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(unrelatedManifest);
        try
        {
            await using var client = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
                .Build();

            Ensure(client.Get<IOrdersContract>() is OrdersProxy,
                "a filtered child should build without reading an unrelated global manifest");
        }
        finally
        {
            unrelatedManifest = null;
            CollectWeakCatalogEntries();
        }
    }

    [Test]
    [NotInParallel]
    public async Task BuildShouldIgnoreRoutesForUnconfiguredClusters()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        ISharpLinkGeneratedClusterRouteManifest? unrelatedRoute = new UnconfiguredRouteManifest();
        SharpLinkGeneratedClusterRouteCatalog.Register(unrelatedRoute);
        try
        {
            await using var client = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
                .Build();

            Ensure(client.Get<IOrdersContract>() is OrdersProxy,
                "unconfigured route manifests must not block a coordinator's configured routes");
        }
        finally
        {
            unrelatedRoute = null;
            CollectWeakCatalogEntries();
        }
    }

    [Test]
    [NotInParallel]
    public async Task FilteredStaticRoutesShouldNotRetainUnconfiguredRouteManifests()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var unrelatedRoute = RegisterUnconfiguredRouteManifest();

        await using (var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build())
        {
            Ensure(client.Get<IOrdersContract>() is OrdersProxy,
                "the configured route must build without retaining unrelated route manifests");
        }

        CollectWeakCatalogEntries();
        Ensure(!unrelatedRoute.IsAlive,
            "a coordinator must not retain a collectible route manifest that contributes no configured route");
    }

    [Test]
    public async Task DynamicRegistrationShouldPreserveStructuredNullAndMissingUnregisterResults()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var nullRegistration = client.RegisterAssembly("plugins", null!);
        Ensure(!nullRegistration.Succeeded &&
               nullRegistration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null dynamic registration must return the shared structured invalid-argument result");

        var missingUnregister = await client.UnregisterAssemblyAsync(
            "plugins", typeof(string).Assembly, TimeSpan.Zero);
        Ensure(!missingUnregister.ReferencesReleased,
            "unregistering an assembly that is not registered must match child false-result semantics");
    }

    [Test]
    public async Task DynamicRegistrationShouldReturnStructuredFailureAfterStop()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var registration = client.RegisterAssembly("plugins", typeof(string).Assembly);
        Ensure(!registration.Succeeded &&
               registration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
            "registration after shutdown must return the structured terminal-state failure before cluster lookup");
    }

    [Test]
    public async Task DynamicReplacementShouldReturnStructuredFailureAfterStop()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var replacement = await client.ReplaceAssemblyAsync(
            "plugins", typeof(string).Assembly, typeof(int).Assembly, TimeSpan.Zero);
        Ensure(!replacement.Succeeded &&
               replacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
            "replacement after shutdown must return the structured terminal-state failure before cluster lookup");
    }

    [Test]
    public async Task DynamicUnregisterShouldReturnFalseAfterStop()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var unregister = await client.UnregisterAssemblyAsync(
            "plugins", typeof(string).Assembly, TimeSpan.Zero);
        Ensure(!unregister.ReferencesReleased,
            "unregistration after shutdown must return the child-compatible false result before cluster lookup");
    }

    [Test]
    public Task EmptySlotShouldRequireExplicitDynamicOptIn()
    {
        var builder = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("dynamic", child => child.UseTransport(new TestClientTransportFactory()));

        return EnsureThrows<InvalidOperationException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task UnknownContractShouldFailWithoutSelectingAnotherCluster()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = client.Get<IUnroutedContract>();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldRejectZeroClustersAndConnectionBudgetOverflow()
    {
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpLinkMultiClusterClientBuilder.Create().Build();
            return Task.CompletedTask;
        });

        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = SharpLinkMultiClusterClientBuilder.Create()
                .Configure(options => options.MaxTotalConfiguredConnections = 1)
                .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
                .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task SingleEndpointSlotsShouldUseTheirFixedConnectionBudget()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child.UseEndpoint(
                Endpoint("orders", 5001),
                static _ => new TestClientTransportFactory()))
            .AddCluster("plugins", child => child.UseEndpoint(
                Endpoint("plugins", 5002),
                static _ => new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "single-endpoint slots fit their configured fixed-client budget");
    }

    [Test]
    public async Task SingleEndpointCollectionsShouldUseTheirFixedConnectionBudget()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child.UseEndpoints(
                new OneShotEndpointEnumerable(Endpoint("orders", 5001)),
                static _ => new TestClientTransportFactory()))
            .AddCluster("plugins", child => child.UseEndpoints(
                new OneShotEndpointEnumerable(Endpoint("plugins", 5002)),
                static _ => new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "one-endpoint collections must use their fixed-client budget without a second enumeration");
    }

    [Test]
    public async Task StaticEndpointClustersShouldUseTheirEffectiveConnectionBudget()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child
                .UseEndpoints(
                    [Endpoint("orders-a", 5001), Endpoint("orders-b", 5002)],
                    static _ => new TestClientTransportFactory())
                .UseCluster(static options =>
                {
                    options.MaxConnections = 4;
                    options.MaxConnectionsPerEndpoint = 1;
                }))
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "a static cluster must count its endpoint-capped connection capacity during coordinator preflight");
    }

    [Test]
    public async Task StopDuringInitialConnectShouldRemainStoppedAfterSharedConnectFaults()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var blocked = new BlockingTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(blocked))
            .Build();

        var connecting = client.ConnectAsync().AsTask();
        await blocked.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync();
        await EnsureThrows<OperationCanceledException>(async () => await connecting);

        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "shutdown must own the terminal state when it races the initial shared connect");
        await client.StopAsync();
    }

    [Test]
    public async Task ConcurrentDynamicUnregisterShouldShareOneCoordinatorOperation()
    {
        var child = new CoordinatedUnregisterClient();
        SharpLinkClusterKey cluster = "plugins";
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var route = new SharpLinkClusterRouteRegistration(
            typeof(IOrdersContract),
            8_101,
            "orders-v1",
            slot,
            TestManifestAssembly);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            new Dictionary<Type, SharpLinkClusterRouteRegistration>
            {
                [typeof(IOrdersContract)] = route
            }.ToFrozenDictionary(),
            []);
        var registrations = (List<DynamicAssemblyRegistration>)(typeof(SharpLinkMultiClusterClient)
            .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!);
        registrations.Add(new DynamicAssemblyRegistration(slot, TestManifestAssembly, Manifest.Instance));

        var first = client.UnregisterAssemblyAsync(
            cluster, TestManifestAssembly, TimeSpan.Zero).AsTask();
        var second = client.UnregisterAssemblyAsync(
            cluster, TestManifestAssembly, TimeSpan.Zero).AsTask();
        child.RejectUnregister(new InvalidOperationException("controlled child unregister failed"));
        var firstFailure = await CaptureExceptionAsync(first);
        var secondFailure = await CaptureExceptionAsync(second);

        Ensure(child.UnregisterCallCount == 1,
            "concurrent coordinator callers must invoke the child unregister operation once");
        Ensure(ReferenceEquals(firstFailure, secondFailure),
            "concurrent coordinator callers must observe the same original failure");
        Ensure(firstFailure is InvalidOperationException { Message: "controlled child unregister failed" },
            "the shared operation must preserve the child failure");
    }

    [Test]
    public async Task ReadyStateReadsShouldNotAllocate()
    {
        SharpLinkClusterKey cluster = "ready";
        var child = new CoordinatedUnregisterClient(SharpLinkConnectionState.Ready);
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)SharpLinkMultiClusterState.Ready);
        _ = client.State;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var readyReads = 0;
        for (var index = 0; index < 100_000; index++)
            readyReads += client.State == SharpLinkMultiClusterState.Ready ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(readyReads == 100_000, "every state read should preserve Ready semantics");
        Ensure(allocated == 0, $"ready state reads allocated {allocated} bytes");
    }

    private static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void CollectWeakCatalogEntries()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _ = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
        _ = SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterUnconfiguredRouteManifest()
    {
        ISharpLinkGeneratedClusterRouteManifest manifest = new UnconfiguredRouteManifest();
        SharpLinkGeneratedClusterRouteCatalog.Register(manifest);
        return new WeakReference(manifest);
    }

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static Assembly CreateTestManifestAssembly()
        => AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SharpLink.MultiClusterClientTests.Manifest"),
            AssemblyBuilderAccess.Run);

    private interface IOrdersContract : IService;
    private interface IUnroutedContract : IService;
    private sealed class OrdersProxy : IOrdersContract;

    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        public static readonly Manifest Instance = new();
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => TestManifestAssembly;
        public string CompileTimeDescriptor => "multi-cluster-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new SharpLinkGeneratedContractDescriptor(
                typeof(IOrdersContract),
                typeof(IOrdersContract).FullName!,
                8_101,
                "orders-v1",
                [],
                static _ => new OrdersProxy(),
                static () => throw new NotSupportedException())
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [];
        public IReadOnlyList<string> Dependencies { get; } = [];
    }

    private sealed class RouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public static readonly RouteManifest Instance = new();
        public Assembly OwnerAssembly => TestManifestAssembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "orders",
                TestManifestAssembly,
                TestManifestAssembly.FullName!)
        ];
    }

    private sealed class ThrowingCodecManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(string).Assembly;
        public string CompileTimeDescriptor => "unrelated-manifest";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs
            => throw new InvalidOperationException("Unrelated manifests must not be read by a filtered child.");
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class UnconfiguredRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTests).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "unconfigured",
                typeof(string).Assembly,
                typeof(string).Assembly.FullName!)
        ];
    }

    private sealed class BlockingTransportFactory : IClientTransportFactory
    {
        internal TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled connect should not continue.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CoordinatedUnregisterClient : ISharpLinkClient, IDynamicAssemblyRegistrationInspector
    {
        private readonly TaskCompletionSource<SharpLinkAssemblyUnregisterResult> _unregister =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _unregisterCallCount;

        internal CoordinatedUnregisterClient(
            SharpLinkConnectionState state = SharpLinkConnectionState.Created)
            => State = state;

        internal int UnregisterCallCount => Volatile.Read(ref _unregisterCallCount);
        public SharpLinkConnectionState State { get; private set; }

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _unregisterCallCount);
            return new ValueTask<SharpLinkAssemblyUnregisterResult>(_unregister.Task);
        }

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(
                new SharpLinkAssemblyRegistrationError(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "not supported")));

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            State = SharpLinkConnectionState.Stopped;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Unhealthy));

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => StopAsync();

        bool IDynamicAssemblyRegistrationInspector.IsDynamicAssemblyRegistered(Assembly assembly)
            => true;

        internal void RejectUnregister(Exception exception)
            => _unregister.TrySetException(exception);
    }

    private sealed class OneShotEndpointEnumerable : IEnumerable<SharpLinkEndpoint>
    {
        private readonly SharpLinkEndpoint _endpoint;
        private int _enumerationCount;

        public OneShotEndpointEnumerable(SharpLinkEndpoint endpoint) => _endpoint = endpoint;

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
                throw new InvalidOperationException("Endpoint source must be enumerated only once.");

            yield return _endpoint;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
