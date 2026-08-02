using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
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
        var rejectedTransport = new ControlledMutationTransportFactory();
        var replacementFailure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            cluster,
            childBuilder => childBuilder.UseTransport(rejectedTransport),
            TimeSpan.Zero).AsTask());
        child.RejectUnregister(new InvalidOperationException("controlled child unregister failed"));
        var firstFailure = await CaptureExceptionAsync(first);
        var secondFailure = await CaptureExceptionAsync(second);

        Ensure(replacementFailure is InvalidOperationException replacementException &&
               replacementException.Message.Contains("lifecycle operation", StringComparison.OrdinalIgnoreCase),
            "slot replacement must reject while assembly unregister/drain owns the generation");
        Ensure(rejectedTransport.DisposeCount == 1,
            "assembly-lifecycle rejection must dispose the unbuilt replacement transport");
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
        // Let tiered PGO finish its instrumented warm-up before measuring the steady-state path.
        for (var index = 0; index < 100_000; index++)
            _ = client.State;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var readyReads = 0;
        for (var index = 0; index < 100_000; index++)
            readyReads += client.State == SharpLinkMultiClusterState.Ready ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(readyReads == 100_000, "every state read should preserve Ready semantics");
        Ensure(allocated == 0, $"ready state reads allocated {allocated} bytes");
    }

    [Test]
    public async Task CreatedStateAddShouldPublishAnUnconnectedSlotAndRoute()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var candidate = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.AddClusterAsync("orders", child => child.UseTransport(candidate));

        Ensure(candidate.ConnectCount == 0,
            "Created-state add must publish a frozen child without connecting it early");
        Ensure(client.GetClusterState("plugins") == SharpLinkConnectionState.Created,
            "runtime add must accept a steady connection budget exactly at the configured limit");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "the newly published child must remain Created until the shared connect");
        Ensure(client.Get<IOrdersContract>() is OrdersProxy,
            "the static contract route must become visible in the same add publication");
    }

    [Test]
    public async Task CreatedStateReplaceShouldSwitchTheUnconnectedSlotAndRetireTheOldChild()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var oldTransport = new ControlledMutationTransportFactory();
        var replacementTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        await client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(replacementTransport),
            TimeSpan.FromSeconds(2));
        var replacementProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(replacementTransport.ConnectCount == 0,
            "Created-state replacement must not connect before the coordinator connects");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "the replacement child remains Created");
        Ensure(!ReferenceEquals(oldProxy.Channel, replacementProxy.Channel),
            "future proxy creation must bind to the replacement child");
        Ensure(oldTransport.DisposeCount == 1,
            "the replaced child must be retired exactly once");
    }

    [Test]
    public async Task CreatedStateRemoveShouldReturnReleasedResultAndUnpublishTheSlot()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var transport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(transport))
            .Build();
        _ = client.Get<IOrdersContract>();

        var result = await client.RemoveClusterAsync("orders", TimeSpan.FromSeconds(2));

        Ensure(result is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "a Created child should be removed and release its resources within the graceful timeout");
        Ensure(transport.DisposeCount == 1, "remove must dispose the retired child exactly once");
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = client.Get<IOrdersContract>();
            return Task.CompletedTask;
        });
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("orders");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ReadyReplaceShouldConnectBeforePublishAndKeepExistingProxyBoundToOldChild()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var oldTransport = new ControlledMutationTransportFactory();
        var replacementTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        await client.ConnectAsync();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var replacement = client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(replacementTransport),
            TimeSpan.FromSeconds(2)).AsTask();
        await replacementTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var proxyWhileCandidateIsConnecting = (OrdersProxy)client.Get<IOrdersContract>();
        Ensure(!replacement.IsCompleted,
            "replacement must remain pending while the candidate connect is blocked");
        Ensure(ReferenceEquals(oldProxy.Channel, proxyWhileCandidateIsConnecting.Channel),
            "the old route must remain published until the replacement candidate is ready");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Ready,
            "pending candidate state must not leak through the public slot state");

        replacementTransport.ReleaseConnect();
        await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        var newProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(replacementTransport.ConnectCount == 1,
            "a Ready coordinator must connect the replacement candidate exactly once");
        Ensure(!ReferenceEquals(oldProxy.Channel, newProxy.Channel),
            "new proxy creation must use the published replacement child");
        Ensure(ReferenceEquals(oldProxy.Channel, proxyWhileCandidateIsConnecting.Channel),
            "an existing proxy must retain its original child binding after replacement");
        Ensure(oldTransport.DisposeCount == 1,
            "the old child must drain and dispose after the replacement snapshot publishes");
    }

    [Test]
    public async Task ReadyReplaceConnectFailureShouldRollbackAndKeepOldRouteUsable()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var oldTransport = new ControlledMutationTransportFactory();
        var failingCandidate = new ControlledMutationTransportFactory(
            connectFailure: new InvalidOperationException("controlled replacement connect failure"));
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        await client.ConnectAsync();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(failingCandidate),
            TimeSpan.FromSeconds(2)).AsTask());
        var proxyAfterFailure = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is InvalidOperationException { Message: "controlled replacement connect failure" },
            "the original candidate connect failure must reach the caller");
        Ensure(ReferenceEquals(oldProxy.Channel, proxyAfterFailure.Channel),
            "failed replacement must leave the old route and child published");
        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Ready,
            "failed replacement must not degrade the existing ready slot");
        Ensure(failingCandidate.DisposeCount == 1,
            "failed candidate resources must roll back exactly once");
        Ensure(oldTransport.DisposeCount == 0,
            "rollback must not retire the still-published old child");
    }

    [Test]
    public async Task RuntimeAddShouldEnforceMaxClustersAndDisposeUnbuiltResources()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options => options.MaxClusters = 1)
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "orders", child => child.UseTransport(rejectedTransport)).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("MaxClusters", StringComparison.Ordinal),
            "runtime add must enforce the configured slot-count limit");
        Ensure(rejectedTransport.DisposeCount == 1,
            "a builder rejected before candidate construction must release its transport");
        Ensure(client.GetClusterState("plugins") == SharpLinkConnectionState.Created,
            "the published snapshot must remain unchanged after MaxClusters rejection");
    }

    [Test]
    public async Task RuntimeAddShouldEnforceSteadyConnectionBudgetAndRollbackCandidate()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .Configure(options =>
            {
                options.MaxClusters = 2;
                options.MaxTotalConfiguredConnections = 1;
            })
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "orders", child => child.UseTransport(rejectedTransport)).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("MaxTotalConfiguredConnections", StringComparison.Ordinal),
            "runtime add must enforce the published steady-state connection budget");
        Ensure(rejectedTransport.DisposeCount == 1,
            "a built candidate rejected by the budget check must be stopped and disposed");
        Ensure(rejectedTransport.ConnectCount == 0,
            "a budget-rejected candidate must not connect or authenticate before deterministic preflight rejection");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("orders");
            return Task.CompletedTask;
        });
        Ensure(client.GetClusterState("plugins") == SharpLinkConnectionState.Ready,
            "budget rollback must retain the original slot");
    }

    [Test]
    public async Task RuntimeDynamicOnlyAddShouldRequireExplicitOptInAndDisposeItsBuilder()
    {
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "dynamic",
            child => child.UseTransport(rejectedTransport)).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("AllowDynamicContracts", StringComparison.Ordinal),
            "a runtime slot without static routes must require explicit dynamic-contract opt-in");
        Ensure(rejectedTransport.DisposeCount == 1,
            "dynamic-only validation failure must dispose its unbuilt transport");
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeManifestFailureShouldRollbackWithoutPublishingTheCandidate()
    {
        ISharpLinkGeneratedClusterRouteManifest? invalidRoute = new InvalidRuntimeRouteManifest();
        SharpLinkGeneratedClusterRouteCatalog.Register(invalidRoute);
        var rejectedTransport = new ControlledMutationTransportFactory();
        try
        {
            await using var client = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                    slot => slot.AllowDynamicContracts = true)
                .Build();

            var failure = await CaptureExceptionAsync(client.AddClusterAsync(
                "invalid-runtime",
                child => child.UseTransport(rejectedTransport)).AsTask());

            Ensure(failure is InvalidOperationException exception &&
                   exception.Message.Contains("compatible generated contract manifest", StringComparison.Ordinal),
                "runtime manifest preparation must preserve a precise validation failure");
            Ensure(rejectedTransport.DisposeCount == 1,
                "manifest preparation failure must release the candidate builder transport");
            await EnsureThrows<ArgumentException>(() =>
            {
                _ = client.GetClusterState("invalid-runtime");
                return Task.CompletedTask;
            });
        }
        finally
        {
            invalidRoute = null;
            CollectWeakCatalogEntries();
        }
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeRouteConflictShouldStopCandidateAndKeepThePublishedRoute()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        ISharpLinkGeneratedClusterRouteManifest? conflictingRoute = new ConflictingRuntimeRouteManifest();
        SharpLinkGeneratedClusterRouteCatalog.Register(conflictingRoute);
        var oldTransport = new ControlledMutationTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        try
        {
            await using var client = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster("orders", child => child.UseTransport(oldTransport))
                .Build();
            var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

            var failure = await CaptureExceptionAsync(client.AddClusterAsync(
                "conflict",
                child => child.UseTransport(rejectedTransport)).AsTask());
            var retainedProxy = (OrdersProxy)client.Get<IOrdersContract>();

            Ensure(failure is InvalidOperationException exception &&
                   exception.Message.Contains("already routed", StringComparison.Ordinal),
                "runtime route conflict must reject the candidate before publication");
            Ensure(rejectedTransport.DisposeCount == 1,
                "route-conflicting candidate must be stopped and disposed");
            Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
                "route conflict rollback must preserve the original route generation");
            Ensure(oldTransport.DisposeCount == 0,
                "route conflict rollback must not retire the published child");
        }
        finally
        {
            conflictingRoute = null;
            CollectWeakCatalogEntries();
        }
    }

    [Test]
    public async Task RuntimeReplaceShouldEnforceBoundedTransitionConnectionBudget()
    {
        var retiredChildren = Enumerable.Range(0, 4)
            .Select(_ => new BlockingRetiredClient())
            .ToArray();
        var initialSlots = retiredChildren
            .Select((child, index) => new SharpLinkClusterSlot(
                $"retired-{index}", child, AllowDynamicContracts: true))
            .ToFrozenDictionary(static slot => slot.Key);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions
            {
                MaxClusters = 8,
                MaxTotalConfiguredConnections = 4
            },
            initialSlots,
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            [],
            configuredConnectionBudget: 4);

        foreach (var slot in initialSlots.Values)
        {
            var removal = await client.RemoveClusterAsync(slot.Key, TimeSpan.Zero);
            Ensure(removal.ForcedStop,
                "each blocked retirement must remain charged to the transition budget");
        }
        foreach (var child in retiredChildren)
            await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.AddClusterAsync(
            "heavy",
            child => child.UseEndpoints(
                Enumerable.Range(0, 4).Select(index => Endpoint($"heavy-{index}", 6000 + index)),
                static _ => new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true);
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)SharpLinkMultiClusterState.Ready);
        var rejectedTransport = new ControlledMutationTransportFactory();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "heavy",
            child => child.UseTransport(rejectedTransport),
            TimeSpan.Zero).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("transition", StringComparison.OrdinalIgnoreCase),
            "replacement must reject a physical old/new overlap above twice the steady budget");
        Ensure(rejectedTransport.DisposeCount == 1,
            "transition-budget rejection must dispose the replacement candidate");
        Ensure(rejectedTransport.ConnectCount == 0,
            "transition-budget rejection must happen before the replacement can connect");
        Ensure(client.GetClusterState("heavy") == SharpLinkConnectionState.Created,
            "transition-budget rollback must preserve the published heavy slot");

        foreach (var child in retiredChildren)
            child.ReleaseStop();
        await client.StopAsync();
    }

    [Test]
    public async Task RuntimeAddDuplicateKeyShouldKeepOriginalRouteAndDisposeRejectedBuilder()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var originalTransport = new ControlledMutationTransportFactory();
        var duplicateTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.AddClusterAsync("orders", child => child.UseTransport(originalTransport));
        var originalProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "orders", child => child.UseTransport(duplicateTransport)).AsTask());
        var proxyAfterFailure = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("already configured", StringComparison.Ordinal),
            "a duplicate runtime key must be rejected deterministically");
        Ensure(duplicateTransport.DisposeCount == 1,
            "the duplicate operation must release its unbuilt transport");
        Ensure(ReferenceEquals(originalProxy.Channel, proxyAfterFailure.Channel),
            "duplicate rejection must preserve the original route generation");
        Ensure(originalTransport.DisposeCount == 0,
            "duplicate rejection must not retire the published child");
    }

    [Test]
    public async Task ConnectingCoordinatorShouldRejectRuntimeMutationWithoutPublishingCandidate()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var blocked = new BlockingTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(blocked))
            .Build();

        var connecting = client.ConnectAsync().AsTask();
        await blocked.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "plugins",
            child => child.UseTransport(rejectedTransport),
            slot => slot.AllowDynamicContracts = true).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("connecting", StringComparison.OrdinalIgnoreCase),
            "runtime slot mutation must be rejected while the coordinator is Connecting");
        Ensure(rejectedTransport.DisposeCount == 1,
            "Connecting rejection must release the unbuilt candidate resources");
        await client.StopAsync();
        await EnsureThrows<OperationCanceledException>(async () => await connecting);
        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "the rejected mutation must not interfere with coordinator shutdown");
    }

    [Test]
    public async Task ConcurrentSameKeyAddsShouldPublishOneCandidateAndDisposeTheLoser()
    {
        var winnerTransport = new ControlledMutationTransportFactory(blockConnect: true);
        var loserTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var winner = client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(winnerTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await winnerTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var loser = client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(loserTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await Task.Delay(50);
        Ensure(!loser.IsCompleted,
            "v1 must serialize a second same-key mutation behind the in-flight candidate");

        winnerTransport.ReleaseConnect();
        await winner.WaitAsync(TimeSpan.FromSeconds(2));
        var loserFailure = await CaptureExceptionAsync(loser.WaitAsync(TimeSpan.FromSeconds(2)));

        Ensure(loserFailure is InvalidOperationException exception &&
               exception.Message.Contains("already configured", StringComparison.Ordinal),
            "the serialized losing add must observe the committed duplicate key");
        Ensure(winnerTransport.ConnectCount == 1 && winnerTransport.DisposeCount == 0,
            "the winning candidate must be connected once and remain coordinator-owned");
        Ensure(loserTransport.ConnectCount == 0 && loserTransport.DisposeCount == 1,
            "the losing unbuilt candidate must never connect and must release its transport");
    }

    [Test]
    public async Task ThrowingMutationLoggerShouldNotFailOrStrandLaterMutations()
    {
        var builder = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true);
        builder.UseLoggerFactoryIfUnset(new ThrowingWriteLoggerFactory());
        await using var client = builder.Build();

        await client.AddClusterAsync(
            "first",
            child => child.UseTransport(new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true);
        await client.AddClusterAsync(
            "second",
            child => child.UseTransport(new TestClientTransportFactory()),
            slot => slot.AllowDynamicContracts = true);

        Ensure(client.GetClusterState("first") == SharpLinkConnectionState.Created &&
               client.GetClusterState("second") == SharpLinkConnectionState.Created,
            "application logger failures must not change mutation results or strand the semaphore");
    }

    [Test]
    public async Task StopRacingRuntimeAddShouldCancelAndDisposeThePendingCandidate()
    {
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var add = client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = client.StopAsync().AsTask();

        await EnsureThrows<OperationCanceledException>(async () => await add);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "global Stop must win a race with an unpublished runtime add");
        Ensure(candidateTransport.DisposeCount == 1,
            "Stop-raced candidate resources must be disposed exactly once");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeReplaceShouldMigrateDynamicAssemblyBeforeSwitchingRoute()
    {
        await RollbackState.TestIsolation.WaitAsync();
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        try
        {
            SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
            SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
            var oldTransport = new ControlledMutationTransportFactory();
            var replacementTransport = new ControlledMutationTransportFactory();
            await using var client = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster("orders", child => child.UseTransport(oldTransport),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            var dynamicAssembly = typeof(RollbackMarker).Assembly;
            var registration = client.RegisterAssembly("orders", dynamicAssembly);
            Ensure(registration.Succeeded, $"dynamic setup registration must succeed: {registration.Error}");
            var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

            await client.ReplaceClusterAsync(
                "orders",
                child => child.UseTransport(replacementTransport),
                TimeSpan.FromSeconds(2));
            var replacementProxy = (OrdersProxy)client.Get<IOrdersContract>();
            var duplicate = client.RegisterAssembly("orders", dynamicAssembly);
            var coordinator = (SharpLinkMultiClusterClient)client;
            var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
                .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator)!;
            var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator)!;

            Ensure(!ReferenceEquals(oldProxy.Channel, replacementProxy.Channel),
                "the static route must switch to the replacement child after dynamic migration succeeds");
            Ensure(registrations.Count == 1 &&
                   ReferenceEquals(registrations[0].Slot, snapshot.Clusters["orders"]) &&
                   ReferenceEquals(registrations[0].Assembly, dynamicAssembly),
                "the coordinator must retarget its dynamic registration to the replacement snapshot slot");
            Ensure(!duplicate.Succeeded &&
                   duplicate.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                "the coordinator dynamic registration catalog must migrate with the replacement slot");
            Ensure(oldTransport.DisposeCount == 1,
                "the old dynamically registered child must retire after migration");
            Ensure(replacementTransport.DisposeCount == 0,
                "the replacement child must remain owned by the coordinator");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    [NotInParallel]
    public async Task RuntimeReplaceDynamicMigrationFailureShouldKeepOldSlotAndRoute()
    {
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        var oldTransport = new ControlledMutationTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("orders", child => child.UseTransport(oldTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        var coordinator = (SharpLinkMultiClusterClient)client;
        var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
            .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        registrations.Add(new DynamicAssemblyRegistration(
            snapshot.Clusters["orders"],
            typeof(string).Assembly,
            new ThrowingCodecManifest()));
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(rejectedTransport),
            TimeSpan.Zero).AsTask());
        var retainedProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("migration", StringComparison.OrdinalIgnoreCase),
            "candidate dynamic registration failure must abort replacement");
        Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
            "dynamic migration rollback must preserve the old public route");
        Ensure(oldTransport.DisposeCount == 0 && rejectedTransport.DisposeCount == 1,
            "migration rollback must retain the old child and dispose only the candidate");
    }

    [Test]
    public async Task DegradedCoordinatorShouldConnectCandidateBeforeRuntimeAddPublication()
    {
        var candidateTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)SharpLinkMultiClusterState.Degraded);

        await client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true);

        Ensure(candidateTransport.ConnectCount == 1,
            "a Degraded coordinator must connect a runtime candidate before publication");
        Ensure(client.GetClusterState("candidate") == SharpLinkConnectionState.Ready,
            "the published candidate must expose its connected state");
    }

    [Test]
    [Arguments(SharpLinkMultiClusterState.Draining)]
    [Arguments(SharpLinkMultiClusterState.Stopped)]
    [Arguments(SharpLinkMultiClusterState.Faulted)]
    public async Task TerminalCoordinatorStateShouldRejectRuntimeMutation(
        SharpLinkMultiClusterState terminalState)
    {
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)terminalState);

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(rejectedTransport),
            slot => slot.AllowDynamicContracts = true).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains(terminalState.ToString(), StringComparison.Ordinal),
            "terminal coordinator states must reject runtime slot mutations explicitly");
        Ensure(rejectedTransport.DisposeCount == 1,
            "a candidate builder rejected by a terminal state must release its resources");
    }

    [Test]
    public async Task CancelledReadyAddShouldRollbackCandidateWithoutPublishingItsSlot()
    {
        var bootstrapTransport = new ControlledMutationTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(bootstrapTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();

        var add = client.AddClusterAsync(
            "candidate",
            child => child.UseTransport(candidateTransport),
            slot => slot.AllowDynamicContracts = true,
            cancellation.Token).AsTask();
        await candidateTransport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await EnsureThrows<OperationCanceledException>(async () => await add);
        Ensure(candidateTransport.DisposeCount == 1,
            "cancellation before publication must stop and dispose the connected candidate generation");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
        Ensure(client.GetClusterState("bootstrap") == SharpLinkConnectionState.Ready,
            "candidate cancellation must leave the existing public snapshot unchanged");
    }

    [Test]
    public async Task CreatedAddCancellationDuringPreparationShouldRollbackBeforePublication()
    {
        var candidateTransport = new ControlledMutationTransportFactory();
        using var cancellation = new CancellationTokenSource();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(client.AddClusterAsync(
            "candidate",
            child => child.UseEndpoints(
                new CancellingEndpointEnumerable(
                    cancellation,
                    Endpoint("candidate", 6501)),
                _ => candidateTransport),
            slot => slot.AllowDynamicContracts = true,
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            "Created-state cancellation during synchronous preparation must reach the caller");
        Ensure(candidateTransport.ConnectCount == 0 && candidateTransport.DisposeCount == 1,
            "the prepared Created candidate must be disposed without connecting");
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState("candidate");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task CreatedReplaceCancellationDuringPreparationShouldKeepTheOldSlot()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var candidateTransport = new ControlledMutationTransportFactory();
        using var cancellation = new CancellationTokenSource();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster("dynamic", child => child.UseTransport(oldTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "dynamic",
            child => child.UseEndpoints(
                new CancellingEndpointEnumerable(
                    cancellation,
                    Endpoint("candidate", 6502)),
                _ => candidateTransport),
            TimeSpan.Zero,
            cancellation.Token).AsTask());

        Ensure(failure is OperationCanceledException,
            "Created-state replacement cancellation during preparation must reach the caller");
        Ensure(candidateTransport.ConnectCount == 0 && candidateTransport.DisposeCount == 1,
            "the cancelled replacement candidate must be disposed without connecting");
        Ensure(oldTransport.DisposeCount == 0 &&
               client.GetClusterState("dynamic") == SharpLinkConnectionState.Created,
            "replacement cancellation must keep the old slot published and owned by the coordinator");
    }

    [Test]
    public async Task CancellationAfterRemovePublicationShouldNotRestoreTheRetiredSlot()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        using var cancellation = new CancellationTokenSource();

        var removal = client.RemoveClusterAsync(
            cluster,
            TimeSpan.FromSeconds(5),
            cancellation.Token).AsTask();
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState(cluster);
            return Task.CompletedTask;
        });
        cancellation.Cancel();

        await EnsureThrows<OperationCanceledException>(async () => await removal);
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState(cluster);
            return Task.CompletedTask;
        });

        var coordinatorStop = client.StopAsync().AsTask();
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!coordinatorStop.IsCompleted,
            "caller cancellation must leave retired cleanup owned by coordinator shutdown");
        child.ReleaseStop();
        await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task TimerRangeExceedingRemoveTimeoutShouldRemainPendingUntilCleanupCompletes()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var removal = client.RemoveClusterAsync(cluster, TimeSpan.MaxValue).AsTask();
        await Task.Delay(50);
        Ensure(!removal.IsCompleted,
            "a timer-range-exceeding graceful timeout must remain pending while calls are active");

        child.ReleaseCalls();
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.ReleaseStop();
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(result is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "huge graceful timeout must complete normally after the retired child drains");
    }

    [Test]
    public async Task ForcedRemoveShouldUnpublishImmediatelyAndCoordinatorStopShouldTrackCleanup()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var removal = await client.RemoveClusterAsync(cluster, TimeSpan.Zero);
        Ensure(removal is { Succeeded: true, ReferencesReleased: false, ForcedStop: true },
            "a zero-timeout remove must report forced cleanup without rolling back publication");
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var coordinatorStop = client.StopAsync().AsTask();
        await Task.Delay(50);
        Ensure(!coordinatorStop.IsCompleted,
            "coordinator StopAsync must keep ownership of a retired child cleanup still in progress");

        child.ReleaseStop();
        await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(child.State == SharpLinkConnectionState.Stopped,
            "retired child cleanup must finish before coordinator StopAsync completes");
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
    private sealed class OrdersProxy(IRpcChannel channel) : IOrdersContract
    {
        internal IRpcChannel Channel { get; } = channel;
    }

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
                static channel => new OrdersProxy(channel),
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

    private sealed class InvalidRuntimeRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTests).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "invalid-runtime",
                typeof(string).Assembly,
                typeof(string).Assembly.FullName!)
        ];
    }

    private sealed class ConflictingRuntimeRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTests).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "conflict",
                TestManifestAssembly,
                TestManifestAssembly.FullName!)
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

    private sealed class CancellingEndpointEnumerable(
        CancellationTokenSource cancellation,
        SharpLinkEndpoint endpoint) : IEnumerable<SharpLinkEndpoint>
    {
        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            cancellation.Cancel();
            yield return endpoint;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingWriteLoggerFactory : ILoggerFactory
    {
        private static readonly ILogger Logger = new ThrowingWriteLogger();

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => Logger;

        public void Dispose() { }

        private sealed class ThrowingWriteLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("controlled logger write failure");
        }
    }

    private sealed class ControlledMutationTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource<bool> _connectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? _connectFailure;
        private int _connectCount;
        private int _disposeCount;

        internal ControlledMutationTransportFactory(
            bool blockConnect = false,
            Exception? connectFailure = null)
        {
            _connectFailure = connectFailure;
            if (!blockConnect)
                _connectRelease.TrySetResult(true);
        }

        internal TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult(true);
            await _connectRelease.Task.WaitAsync(cancellationToken);
            if (_connectFailure is not null)
                throw _connectFailure;
            return await _inner.ConnectAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await _inner.DisposeAsync();
        }

        internal void ReleaseConnect() => _connectRelease.TrySetResult(true);
    }

    private sealed class BlockingRetiredClient : ISharpLinkClient, ISharpLinkClientDrainInspector
    {
        private readonly TaskCompletionSource _stop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls = 1;

        internal TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State { get; private set; } = SharpLinkConnectionState.Ready;
        int ISharpLinkClientDrainInspector.ActiveCallCount => Volatile.Read(ref _activeCalls);
        int ISharpLinkClientDrainInspector.ActiveStreamCount => 0;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            State = SharpLinkConnectionState.Draining;
            StopStarted.TrySetResult();
            return cancellationToken.CanBeCanceled
                ? new ValueTask(_stop.Task.WaitAsync(cancellationToken))
                : new ValueTask(_stop.Task);
        }

        public ValueTask DisposeAsync() => StopAsync();

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(
                new SharpLinkAssemblyRegistrationError(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "not supported")));

        internal void ReleaseStop()
        {
            State = SharpLinkConnectionState.Stopped;
            _stop.TrySetResult();
        }

        internal void ReleaseCalls() => Volatile.Write(ref _activeCalls, 0);
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
