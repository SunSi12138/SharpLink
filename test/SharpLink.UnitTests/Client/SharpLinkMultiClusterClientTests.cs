using System.Reflection;
using System.Reflection.Emit;
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
