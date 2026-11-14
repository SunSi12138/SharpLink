using System.Linq;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterDiscoveryTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task IsolatedDiscoverySourcesShouldBeCapturedOnceAndFrozenIntoChildren()
    {
        var order = new List<string>();
        var manifests = new List<ISharpLinkGeneratedAssemblyManifest>
        {
            Manifest.Instance
        };
        var routes = new List<ISharpLinkGeneratedClusterRouteManifest>
        {
            RouteManifest.Instance
        };
        var routeSource = new CountingRouteSource(() =>
        {
            order.Add("route");
            return routes;
        });
        var manifestSource = new CountingManifestSource(() =>
        {
            Ensure(routeSource.CreateSnapshotCount == 1,
                "route discovery and selected module bootstrap must precede manifest capture");
            order.Add("manifest");
            return manifests;
        });

        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .UseGeneratedDiscoverySources(manifestSource, routeSource)
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .AddCluster(
                "payments",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        manifests.Clear();
        routes.Clear();

        Ensure(order.SequenceEqual(["route", "manifest"]),
            "multi-cluster Compile must capture route then assembly discovery once");
        Ensure(routeSource.CreateSnapshotCount == 1 && manifestSource.CreateSnapshotCount == 1,
            "coordinator Compile must query each discovery source exactly once");
        var orders = client.CreateProxy<IOrdersContract>().Service as OrdersProxy ??
            throw new Exception("orders child must materialize its routed proxy");
        var payments = GetChildClient(client, "payments");
        Ensure(orders.Channel.RuntimeContext.Codecs.GetCodec<OrdersValue>() is TestCodec<OrdersValue>,
            "the routed child Runtime must consume the codec from its own frozen manifest closure");
        EnsureCodecIsMissing<OrdersValue>(payments);

        await client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(new TestClientTransportFactory()),
            TimeSpan.FromSeconds(2));
        var replacementOrders = client.CreateProxy<IOrdersContract>().Service as OrdersProxy ??
            throw new Exception("replacement orders child must materialize its routed proxy");
        Ensure(replacementOrders.Channel.RuntimeContext.Codecs.GetCodec<OrdersValue>() is TestCodec<OrdersValue>,
            "replacement must compile from the slot's frozen plan snapshot after caller lists are cleared");
        await client.StopAsync();
        Ensure(routeSource.CreateSnapshotCount == 1 && manifestSource.CreateSnapshotCount == 1,
            "coordinator runtime and Stop must not re-query initial bootstrap sources");
    }

    [Test]
    public async Task RuntimeChildCompileShouldCaptureEachExplicitDiscoverySourceOnce()
    {
        var order = new List<string>();
        var routeSource = new CountingRouteSource(() =>
        {
            order.Add("route");
            return [RouteManifest.Instance];
        });
        var manifestSource = new CountingManifestSource(() =>
        {
            order.Add("manifest");
            return [Manifest.Instance];
        });

        var prepared = SharpLinkMultiClusterClientBuilder.PrepareRuntimeCluster(
            "orders",
            SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTransport(new TestClientTransportFactory()),
            allowDynamicContracts: false,
            manifestSource,
            routeSource);
        try
        {
            Ensure(order.SequenceEqual(["route", "manifest"]) &&
                   routeSource.CreateSnapshotCount == 1 && manifestSource.CreateSnapshotCount == 1,
                "a runtime child Compile must take one ordered point-in-time discovery snapshot");
            Ensure(prepared.StaticRoutes.ContainsKey(typeof(IOrdersContract)) &&
                   prepared.Slot.StaticManifests is { Count: 1 } staticManifests &&
                   ReferenceEquals(staticManifests[0], Manifest.Instance),
                "the prepared child must own only its routed frozen manifest closure");
            var proxy = prepared.Slot.Client.CreateProxy<IOrdersContract>().Service as OrdersProxy ??
                throw new Exception("runtime child must materialize its routed proxy");
            Ensure(proxy.Channel.RuntimeContext.Codecs.GetCodec<OrdersValue>() is TestCodec<OrdersValue>,
                "the runtime child must actually materialize its proxy and Runtime Codec from that closure");
        }
        finally
        {
            await prepared.Slot.Client.DisposeAsync();
        }

        Ensure(routeSource.CreateSnapshotCount == 1 && manifestSource.CreateSnapshotCount == 1,
            "runtime child disposal must not retain or re-query either cold discovery source");
    }

    [Test]
    public async Task StaticRouteShouldCreateTheTargetChildProxyAndConnectEverySlot()
    {
        var ordersTransport = new TestClientTransportFactory();
        var paymentsTransport = new TestClientTransportFactory();

        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(ordersTransport))
            .AddCluster("payments", child => child.UseTransport(paymentsTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var proxy = client.CreateProxy<IOrdersContract>().Service;
        Ensure(proxy is OrdersProxy, "the local factory should create the proxy from the captured routed child client");
        await client.ConnectAsync();

        Ensure(client.State == SharpLinkMultiClusterState.Ready, "all slots should be ready after shared connect");
        Ensure(ordersTransport.ConnectCount == 1, "orders child should connect once");
        Ensure(paymentsTransport.ConnectCount == 1, "payments child should connect once");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Ready, "orders slot state");
    }

    [Test]
    public async Task FilteredStaticRoutesShouldIgnoreUnrelatedGlobalManifests()
    {
        var unrelatedManifest = new ThrowingCodecManifest();
        await using var client = CreateBuilder(
                [Manifest.Instance, unrelatedManifest],
                [RouteManifest.Instance])
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        Ensure(client.CreateProxy<IOrdersContract>().Service is OrdersProxy,
            "a filtered child should build without reading an unrelated manifest snapshot entry");
    }

    [Test]
    public async Task RepeatedFactoriesShouldShareExactSiteAndHandleServiceAccessShouldNotAllocate()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        var first = client.CreateProxy<IOrdersContract>();
        var second = client.CreateProxy<IOrdersContract>();
        Ensure(!ReferenceEquals(first.Service, second.Service) &&
               ReferenceEquals(((OrdersProxy)first.Service).Channel, ((OrdersProxy)second.Service).Channel),
            "local construction creates independent handles sharing the exact registration/site");
        var service = first.Service;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            Ensure(ReferenceEquals(first.Service, service), "a handle retains its originally constructed service");
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        Ensure(allocatedAfter == allocatedBefore,
            "steady-state handle Service access must not allocate or reconstruct a proxy/channel");
    }

    [Test]
    public async Task BuildShouldIgnoreRoutesForUnconfiguredClusters()
    {
        var unrelatedRoute = new UnconfiguredRouteManifest();
        await using var client = CreateBuilder(
                [Manifest.Instance],
                [RouteManifest.Instance, unrelatedRoute])
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        Ensure(client.CreateProxy<IOrdersContract>().Service is OrdersProxy,
            "unconfigured route manifests must not block a coordinator's configured routes");
    }

    [Test]
    // This is the intentional weak global-catalog retention test; ordinary builders use fixed sources.
    [NotInParallel("generated-catalog")]
    public async Task FilteredStaticRoutesShouldNotRetainUnconfiguredRouteManifests()
    {
        var assemblyCountBefore = RollbackTestIsolation.AssemblyManifestCount;
        var routeCountBefore = RollbackTestIsolation.RouteManifestCount;
        var assemblyManifestWasRegistered = RollbackTestIsolation.ContainsManifest(Manifest.Instance);
        var routeManifestWasRegistered = RollbackTestIsolation.ContainsManifest(RouteManifest.Instance);
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        WeakReference? unrelatedRoute = null;
        try
        {
            unrelatedRoute = RegisterUnconfiguredRouteManifest();

            await using (var client = SharpLinkMultiClusterClientBuilder.Create()
                .DisableRequestTimeout()
                .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
                .Build())
            {
                Ensure(client.CreateProxy<IOrdersContract>().Service is OrdersProxy,
                    "the configured route must build without retaining unrelated route manifests");
            }

            CollectWeakCatalogEntries();
            Ensure(!unrelatedRoute.IsAlive,
                "a coordinator must not retain a collectible route manifest that contributes no configured route");
        }
        finally
        {
            if (!assemblyManifestWasRegistered)
                _ = RollbackTestIsolation.RemoveManifestFromCatalog(Manifest.Instance);
            if (!routeManifestWasRegistered)
                _ = RollbackTestIsolation.RemoveManifestFromCatalog(RouteManifest.Instance);
            if (unrelatedRoute?.Target is ISharpLinkGeneratedClusterRouteManifest remainingRoute)
                _ = RollbackTestIsolation.RemoveManifestFromCatalog(remainingRoute);
            CollectWeakCatalogEntries();
            Ensure(RollbackTestIsolation.AssemblyManifestCount <= assemblyCountBefore &&
                   RollbackTestIsolation.RouteManifestCount <= routeCountBefore,
                "the weak global-catalog test must restore its identities without growing either catalog");
        }
    }
}
