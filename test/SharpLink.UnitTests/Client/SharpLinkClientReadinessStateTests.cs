using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessSharedSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessStateSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientReadinessStateTests
{
    [Test]
    public async Task SnapshotValueShouldPreserveEqualityAndMeetsTargetInvariants()
    {
        var defaultSnapshot = default(SharpLinkClientReadinessSnapshot);
        var satisfied = new SharpLinkClientReadinessSnapshot(
            SharpLinkConnectionState.Ready,
            ActiveEndpoints: 3,
            ReadyEndpoints: 2,
            ReadyConnections: 4,
            TargetReadyEndpoints: 2);
        var equal = new SharpLinkClientReadinessSnapshot(
            SharpLinkConnectionState.Ready,
            ActiveEndpoints: 3,
            ReadyEndpoints: 2,
            ReadyConnections: 4,
            TargetReadyEndpoints: 2);

        Ensure(defaultSnapshot == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Created, 0, 0, 0, 0),
            "the default value must be the empty Created snapshot");
        Ensure(!defaultSnapshot.MeetsTarget,
            "an empty topology must not meet a zero target");
        Ensure(satisfied.MeetsTarget,
            "a Ready snapshot with connections and enough ready endpoints must meet its target");
        Ensure(satisfied == equal && satisfied.GetHashCode() == equal.GetHashCode(),
            "record-struct equality must include every readiness field");
        Ensure(satisfied != equal with { ReadyConnections = 3 },
            "changing one readiness field must change value equality");

        var unsatisfied = new[]
        {
            satisfied with { ReadyEndpoints = 1 },
            satisfied with { State = SharpLinkConnectionState.Reconnecting },
            satisfied with { State = SharpLinkConnectionState.Draining },
            satisfied with { TargetReadyEndpoints = 0 },
            satisfied with { ReadyConnections = 0 }
        };
        for (var index = 0; index < unsatisfied.Length; index++)
            Ensure(!unsatisfied[index].MeetsTarget, $"unsatisfied readiness case {index}");

        await Task.CompletedTask;
    }

    [Test]
    public async Task LegacyThirdPartyClientShouldUseExplicitReadinessDefaultsAndValidateTheMinimum()
    {
        ISharpLinkClient client = new LegacyThirdPartyClient();

        var snapshotFailure = CaptureException(() => client.GetReadinessSnapshot());
        var waitFailure = await CaptureExceptionAsync(client.WaitForReadinessAsync(1).AsTask());
        var validationFailure = CaptureException(() => client.WaitForReadinessAsync(0));

        Ensure(snapshotFailure is NotSupportedException snapshotNotSupported &&
               snapshotNotSupported.Message.Contains("does not expose endpoint readiness", StringComparison.Ordinal),
            "the default snapshot member must reject unknown third-party topology data explicitly");
        Ensure(waitFailure is NotSupportedException waitNotSupported &&
               waitNotSupported.Message.Contains("does not support endpoint readiness waits", StringComparison.Ordinal),
            "the default wait member must reject unsupported third-party waits explicitly");
        Ensure(validationFailure is ArgumentOutOfRangeException { ParamName: "minimumReadyEndpoints" },
            "the default wait member must validate its positive minimum before reporting unsupported readiness");

        await client.DisposeAsync();
    }

    [Test]
    public async Task FixedClientShouldPublishExactCreatedAndConnectedFacts()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var created = client.GetReadinessSnapshot();
        Ensure(created == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Created,
                ActiveEndpoints: 1,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 1),
            "a fixed client must publish its one configured endpoint before connecting");
        Ensure(!created.MeetsTarget, "a Created fixed client must not meet its target");

        await client.ConnectAsync();

        var connected = client.GetReadinessSnapshot();
        Ensure(connected == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready,
                ActiveEndpoints: 1,
                ReadyEndpoints: 1,
                ReadyConnections: 1,
                TargetReadyEndpoints: 1),
            "a connected fixed client must publish its exact endpoint and connection counts");
        Ensure(connected.MeetsTarget, "the connected fixed client must meet its configured target");
    }

    [Test]
    public async Task FixedClientShouldPublishEveryConfiguredReadyConnection()
    {
        var transport = new ControlledSequenceTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            }));

        await client.ConnectAsync();

        var snapshot = client.GetReadinessSnapshot();
        Ensure(snapshot == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready,
                ActiveEndpoints: 1,
                ReadyEndpoints: 1,
                ReadyConnections: 2,
                TargetReadyEndpoints: 1),
            "a fixed two-connection pool must publish both ready connections exactly");
        Ensure(snapshot.MeetsTarget && transport.ConnectCount == 2,
            "ConnectAsync must establish the configured two-connection minimum before returning");
    }

    [Test]
    public async Task FixedReadinessWaitShouldSurviveDisconnectAndCompleteAfterSameClientReconnects()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ControlledSequenceTransportFactory(blockLaterAttempts: true);
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(timeProvider);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseReconnectJitterForTesting(new FixedReadinessReconnectJitter(
                TimeSpan.FromMilliseconds(100)));
        });
        await client.ConnectAsync();
        var firstConnection = await transport.FirstConnectionCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await firstConnection.DisposeAsync();
        var disconnected = await WaitForReadinessSnapshotAsync(
            client,
            static snapshot =>
                snapshot.State == SharpLinkConnectionState.Reconnecting &&
                snapshot.ReadyEndpoints == 0 &&
                snapshot.ReadyConnections == 0);
        Ensure(disconnected == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Reconnecting,
                ActiveEndpoints: 1,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 1),
            "a disconnected fixed client must publish Reconnecting with zero readiness");

        var readiness = client.WaitForReadinessAsync(1).AsTask();
        await transport.LaterAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!readiness.IsCompleted,
            "a readiness wait must remain pending rather than fail while the same client reconnects");

        transport.ReleaseLaterAttempts();
        var reconnected = await readiness.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(reconnected == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready,
                ActiveEndpoints: 1,
                ReadyEndpoints: 1,
                ReadyConnections: 1,
                TargetReadyEndpoints: 1),
            "the pending wait must complete from the same client's replacement connection");
        Ensure(reconnected.MeetsTarget && client.State == SharpLinkConnectionState.Ready,
            "the replacement publication must restore fixed-client readiness");
    }

    [Test]
    public async Task InitialTransportFailureShouldReachConnectAndReadinessWaitUnchangedThenRecover()
    {
        var expectedFailure = new InvalidOperationException("deterministic initial transport failure");
        var transport = new ControlledSequenceTransportFactory(
            blockFirstAttempt: true,
            firstFailure: expectedFailure,
            blockLaterAttempts: true);
        await using var client = ClientBuilderTestHelper.Build(transport);

        var connect = client.ConnectAsync().AsTask();
        await transport.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readiness = client.WaitForReadinessAsync(1).AsTask();
        transport.ReleaseFirstAttempt();

        var connectFailure = await CaptureExceptionAsync(connect);
        var readinessFailure = await CaptureExceptionAsync(readiness);
        Ensure(ReferenceEquals(connectFailure, expectedFailure) &&
               ReferenceEquals(readinessFailure, expectedFailure),
            "the readiness wait must propagate the exact shared ConnectAsync failure instance unchanged");
        Ensure(client.GetReadinessSnapshot() == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Faulted,
                ActiveEndpoints: 1,
                ReadyEndpoints: 0,
                ReadyConnections: 0,
                TargetReadyEndpoints: 1),
            "an initial transport failure must publish a Faulted zero-readiness snapshot");

        var recoveryWait = client.WaitForReadinessAsync(1).AsTask();
        await transport.LaterAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var recoveryConnect = client.ConnectAsync().AsTask();
        Ensure(!recoveryWait.IsCompleted && !recoveryConnect.IsCompleted,
            "new readiness and ConnectAsync callers must join the pending recovery attempt");

        transport.ReleaseLaterAttempts();
        await recoveryConnect.WaitAsync(TimeSpan.FromSeconds(2));
        var recovered = await recoveryWait.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(recovered == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready,
                ActiveEndpoints: 1,
                ReadyEndpoints: 1,
                ReadyConnections: 1,
                TargetReadyEndpoints: 1),
            "a subsequent wait and ConnectAsync call must recover the same fixed client");
        Ensure(recovered.MeetsTarget && transport.ConnectCount == 2,
            "recovery must use exactly one replacement transport attempt");
    }

    [Test]
    public async Task AvailabilityTransitionsShouldNormalizeStaleRequestsAndWakeTerminalGenerations()
    {
        var transport = new ControlledSequenceTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        client.PublishReadinessFacts(ReadyFacts);
        client.TransitionToForTesting(SharpLinkConnectionState.Ready);
        var ready = client.ReadinessPublicationForTesting;
        Ensure(ready.Snapshot == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Ready, 1, 1, 1, 1),
            "ready facts followed by Ready must publish one coherent Ready snapshot");

        client.PublishReadinessFacts(NotReadyFacts);
        var staleReadyInput = client.ReadinessPublicationForTesting;
        Ensure(ready.Changed.Task.IsCompleted,
            "publishing zero-ready facts must wake the prior Ready generation");
        client.TransitionToForTesting(SharpLinkConnectionState.Ready);
        var reconnecting = client.ReadinessPublicationForTesting;
        Ensure(staleReadyInput.Changed.Task.IsCompleted &&
               reconnecting.Snapshot == new SharpLinkClientReadinessSnapshot(
                   SharpLinkConnectionState.Reconnecting, 1, 0, 0, 1),
            "a stale Ready request with zero ready facts must normalize to Reconnecting");

        client.PublishReadinessFacts(ReadyFacts);
        var staleUnavailableInput = client.ReadinessPublicationForTesting;
        Ensure(reconnecting.Changed.Task.IsCompleted,
            "publishing restored ready facts must wake the Reconnecting generation");
        client.TransitionToForTesting(SharpLinkConnectionState.Reconnecting);
        var restoredReady = client.ReadinessPublicationForTesting;
        Ensure(staleUnavailableInput.Changed.Task.IsCompleted &&
               restoredReady.Snapshot == new SharpLinkClientReadinessSnapshot(
                   SharpLinkConnectionState.Ready, 1, 1, 1, 1),
            "a stale Reconnecting request with ready facts must normalize back to Ready");

        client.TransitionToForTesting(SharpLinkConnectionState.Faulted);
        Ensure(ReferenceEquals(restoredReady, client.ReadinessPublicationForTesting) &&
               !restoredReady.Changed.Task.IsCompleted,
            "a stale Faulted request with ready facts must preserve Ready without a redundant publication");
        client.TransitionToForTesting(SharpLinkConnectionState.Connecting);
        client.TransitionToForTesting(SharpLinkConnectionState.Draining);
        Ensure(ReferenceEquals(restoredReady, client.ReadinessPublicationForTesting) &&
               restoredReady.Snapshot.State == SharpLinkConnectionState.Ready,
            "stale non-stop Connecting or Draining requests cannot hide a currently routable connection");

        transport.BlockDispose();
        var stop = client.StopAsync().AsTask();
        ClientReadinessPublication? draining = null;
        try
        {
            await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            draining = client.ReadinessPublicationForTesting;
            Ensure(restoredReady.Changed.Task.IsCompleted &&
                   draining.Snapshot == new SharpLinkClientReadinessSnapshot(
                       SharpLinkConnectionState.Draining, 1, 0, 0, 1),
                "Stop must publish terminal Draining zero-readiness and wake the normalized Ready generation");
        }
        finally
        {
            transport.ReleaseDispose();
        }
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = client.ReadinessPublicationForTesting;
        Ensure(draining is not null && draining.Changed.Task.IsCompleted &&
               stopped.Snapshot.State == SharpLinkConnectionState.Stopped &&
               !stopped.Changed.Task.IsCompleted,
            "Stopped must publish after Draining and own the next readiness generation");
        client.TransitionToForTesting(SharpLinkConnectionState.Draining);
        client.TransitionToForTesting(SharpLinkConnectionState.Ready);
        client.TransitionToForTesting(SharpLinkConnectionState.Reconnecting);
        client.TransitionToForTesting(SharpLinkConnectionState.Faulted);
        Ensure(ReferenceEquals(stopped, client.ReadinessPublicationForTesting) &&
               client.State == SharpLinkConnectionState.Stopped,
            "late lifecycle requests must never move a terminal Client back from Stopped");
    }
}
