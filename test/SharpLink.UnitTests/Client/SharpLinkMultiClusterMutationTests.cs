using System.Collections.Frozen;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterMutationTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    // This exercise intentionally keeps the public default-global cold path.
    [NotInParallel("generated-catalog")]
    public async Task CreatedStateAddShouldPublishAnUnconnectedSlotAndRoute()
    {
        // Other runtime-mutation tests inject fixed sources through the internal compile seam.
        var assemblyCountBefore = RollbackTestIsolation.AssemblyManifestCount;
        var routeCountBefore = RollbackTestIsolation.RouteManifestCount;
        var assemblyManifestWasRegistered = RollbackTestIsolation.ContainsManifest(Manifest.Instance);
        var routeManifestWasRegistered = RollbackTestIsolation.ContainsManifest(RouteManifest.Instance);
        SharpLinkGeneratedAssemblyCatalog.Register(Manifest.Instance);
        SharpLinkGeneratedClusterRouteCatalog.Register(RouteManifest.Instance);
        try
        {
            var candidate = new ControlledMutationTransportFactory();
            await using var client = CreateDynamicBuilder()
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
        finally
        {
            if (!assemblyManifestWasRegistered)
                _ = RollbackTestIsolation.RemoveManifestFromCatalog(Manifest.Instance);
            if (!routeManifestWasRegistered)
                _ = RollbackTestIsolation.RemoveManifestFromCatalog(RouteManifest.Instance);
            Ensure(RollbackTestIsolation.AssemblyManifestCount <= assemblyCountBefore &&
                   RollbackTestIsolation.RouteManifestCount <= routeCountBefore,
                "the public default-global mutation test must not grow either live catalog");
        }
    }

    [Test]
    public async Task CreatedStateReplaceShouldSwitchTheUnconnectedSlotAndRetireTheOldChild()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var replacementTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
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
        var transport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
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
        var oldTransport = new ControlledMutationTransportFactory();
        var replacementTransport = new ControlledMutationTransportFactory(blockConnect: true);
        await using var client = CreateStaticBuilder()
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
        var oldTransport = new ControlledMutationTransportFactory();
        var failingCandidate = new ControlledMutationTransportFactory(
            connectFailure: new InvalidOperationException("controlled replacement connect failure"));
        await using var client = CreateStaticBuilder()
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
    public async Task PrepareReplacementClusterShouldTransferItsChildAfterSuccessfulPreparation()
    {
        var replacementTransport = new ControlledMutationTransportFactory();
        var existingSlot = new SharpLinkClusterSlot(
            "replacement",
            new CoordinatedUnregisterClient(),
            AllowDynamicContracts: true);

        var prepared = SharpLinkMultiClusterClientBuilder.PrepareReplacementCluster(
            existingSlot,
            SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTransport(replacementTransport));

        Ensure(replacementTransport.DisposeCount == 0,
            "successful replacement preparation must transfer its child instead of cleaning it");
        await prepared.Slot.Client.DisposeAsync();
        Ensure(replacementTransport.DisposeCount == 1,
            "the prepared replacement caller must own and dispose the transferred child");
    }

    [Test]
    public async Task RuntimeAddShouldEnforceMaxClustersAndDisposeUnbuiltResources()
    {
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .Configure(options => options.MaxClusters = 1)
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
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
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .Configure(options =>
            {
                options.MaxClusters = 2;
                options.MaxTotalConfiguredConnections = 1;
            })
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
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
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
            "dynamic",
            child => child.UseTransport(rejectedTransport)).AsTask());

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("AllowDynamicContracts", StringComparison.Ordinal),
            "a runtime slot without static routes must require explicit dynamic-contract opt-in");
        Ensure(rejectedTransport.DisposeCount == 1,
            "dynamic-only validation failure must dispose its unbuilt transport");
    }

    [Test]
    public async Task RuntimeManifestFailureShouldRollbackWithoutPublishingTheCandidate()
    {
        var invalidRoute = new InvalidRuntimeRouteManifest();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("bootstrap", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(
            client,
            "invalid-runtime",
            child => child.UseTransport(rejectedTransport),
            manifests: [],
            routes: [invalidRoute]).AsTask());

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

    [Test]
    public async Task RuntimeRouteConflictShouldStopCandidateAndKeepThePublishedRoute()
    {
        var conflictingRoute = new ConflictingRuntimeRouteManifest();
        var oldTransport = new ControlledMutationTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(oldTransport))
            .Build();
        var oldProxy = (OrdersProxy)client.Get<IOrdersContract>();
        await client.ConnectAsync();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(
            client,
            "conflict",
            child => child.UseTransport(rejectedTransport),
            manifests: [Manifest.Instance],
            routes: [conflictingRoute]).AsTask());
        var retainedProxy = (OrdersProxy)client.Get<IOrdersContract>();

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("already routed", StringComparison.Ordinal),
            "runtime route conflict must reject the candidate before publication");
        Ensure(rejectedTransport.DisposeCount == 1,
            "route-conflicting candidate must be stopped and disposed");
        Ensure(rejectedTransport.ConnectCount == 0,
            "an immutable route conflict must be rejected before the candidate connects");
        Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
            "route conflict rollback must preserve the original route generation");
        Ensure(oldTransport.DisposeCount == 0,
            "route conflict rollback must not retire the published child");
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

        await AddClusterWithFixedDiscoveryAsync(client,
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
            child => child.DisableRequestTimeout().UseTransport(rejectedTransport),
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
        var originalTransport = new ControlledMutationTransportFactory();
        var duplicateTransport = new ControlledMutationTransportFactory();
        await using var client = CreateDynamicBuilder()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await AddClusterWithFixedDiscoveryAsync(client, "orders", child => child.UseTransport(originalTransport));
        var originalProxy = (OrdersProxy)client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(AddClusterWithFixedDiscoveryAsync(client,
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
}
