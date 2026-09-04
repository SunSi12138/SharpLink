using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterDynamicAssemblyTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task DynamicRegistrationShouldPreserveStructuredNullAndMissingUnregisterResults()
    {
        await using var client = CreateDynamicBuilder()
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
        await using var client = CreateDynamicBuilder()
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
        await using var client = CreateDynamicBuilder()
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
        await using var client = CreateDynamicBuilder()
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
            childBuilder => childBuilder.DisableRequestTimeout().UseTransport(rejectedTransport),
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
    // The rollback plugin exposes a process-wide environment switch and disposal state.
    [NotInParallel("rollback-plugin")]
    public async Task RuntimeReplaceShouldMigrateDynamicAssemblyBeforeSwitchingRoute()
    {
        await RollbackState.TestIsolation.WaitAsync();
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        try
        {
            var oldTransport = new ControlledMutationTransportFactory();
            var replacementTransport = new ControlledMutationTransportFactory();
            await using var client = CreateStaticBuilder()
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
    // The rollback plugin exposes process-wide construction gates and environment switches.
    [NotInParallel("rollback-plugin")]
    public async Task DynamicRegistrationShouldRejectASlotChangedWhileItsManifestLoads()
    {
        await RollbackState.TestIsolation.WaitAsync();
        var manifestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RollbackState.ManifestConstructionStarted = manifestStarted;
        RollbackState.ManifestConstructionRelease = manifestRelease;
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        SharpLinkClusterKey cluster = "plugins";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        try
        {
            var registration = LongRunningTestWorker.Run(() =>
                client.RegisterAssembly(cluster, typeof(RollbackMarker).Assembly));
            await manifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var removal = await client.RemoveClusterAsync(cluster, TimeSpan.Zero);
            Ensure(removal is { Succeeded: true, ForcedStop: true },
                "the concurrent remove must publish while manifest loading is paused");
            manifestRelease.TrySetResult();

            var result = await registration.WaitAsync(TimeSpan.FromSeconds(2));
            var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            Ensure(!result.Succeeded &&
                   result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "registration must reject a slot that changed while its manifest loaded");
            Ensure(child.RegisterAssemblyCallCount == 0 && registrations.Count == 0,
                "registration must not reach or retain the retired child");

            child.ReleaseStop();
            await client.StopAsync();
        }
        finally
        {
            manifestRelease.TrySetResult();
            child.ReleaseStop();
            RollbackState.ManifestConstructionStarted = null;
            RollbackState.ManifestConstructionRelease = null;
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task RuntimeReplaceDynamicMigrationFailureShouldKeepOldSlotAndRoute()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
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
}
