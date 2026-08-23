using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientReadinessTests
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

    [Test]
    public async Task SatisfiedFixedReadinessWaitShouldCompleteSynchronously()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var wait = client.WaitForReadinessAsync(1);

        Ensure(wait.IsCompletedSuccessfully,
            "an already-satisfied readiness wait must use the synchronous ValueTask fast path");
        var observed = await wait;
        Ensure(observed == client.GetReadinessSnapshot() && observed.MeetsTarget,
            "the synchronous wait must return the exact satisfying publication");
        Ensure(transport.ConnectCount == 1,
            "an already-satisfied wait must not start another connection attempt");
    }

    [Test]
    public async Task FixedClientShouldRejectImpossibleThresholdBeforeConnectingOrCancellation()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = CaptureException(() => client.WaitForReadinessAsync(2, cancellation.Token));

        Ensure(failure is ArgumentOutOfRangeException { ParamName: "minimumReadyEndpoints" },
            "fixed readiness must reject a threshold above its configured maximum");
        Ensure(transport.ConnectCount == 0,
            "threshold validation must fail before cancellation handling or connection startup");
    }

    [Test]
    public async Task PreCanceledWaitShouldWinOverAnAlreadySatisfiedSnapshot()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = CaptureException(() => client.WaitForReadinessAsync(1, cancellation.Token));

        Ensure(failure is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token,
            "entry cancellation must be observed before the satisfied fast path");
        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1,
            "canceling a readiness observation must not disturb the ready client");
    }

    [Test]
    public async Task CancelingOneReadinessWaitShouldNotCancelTheSharedConnectOrAnotherWaiter()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        using var cancellation = new CancellationTokenSource();

        var canceledWaiter = client.WaitForReadinessAsync(1, cancellation.Token).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var survivingWaiter = client.WaitForReadinessAsync(1).AsTask();
        cancellation.Cancel();

        var cancellationFailure = await CaptureExceptionAsync(canceledWaiter);
        Ensure(cancellationFailure is OperationCanceledException,
            "the canceled readiness waiter must observe only its caller cancellation");
        Ensure(!survivingWaiter.IsCompleted && client.State == SharpLinkConnectionState.Connecting,
            "another waiter and the shared client-owned connect must remain pending");
        Ensure(transport.ConnectCount == 1,
            "concurrent readiness waiters must join one shared initial connection attempt");

        transport.ReleaseConnect();
        var observed = await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(observed.MeetsTarget && observed.ReadyConnections == 1,
            "the surviving waiter must complete from the shared connection publication");
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "caller cancellation must not stop or fault the client");
    }

    [Test]
    public async Task StoppingShouldWakeAPendingReadinessWaitWithConnectionClosed()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await client.ConnectAsync();
        client.PublishReadinessFacts(new ClientReadinessFacts(
            ActiveEndpoints: 1,
            ReadyEndpoints: 0,
            ReadyConnections: 0,
            TargetReadyEndpoints: 1));
        var pendingPublication = client.ReadinessPublicationForTesting;

        var waiter = client.WaitForReadinessAsync(1).AsTask();
        Ensure(!waiter.IsCompleted,
            "the zero-ready testing publication must leave the readiness waiter pending");
        Ensure(!client.ReadySignalForTesting.IsCompleted,
            "zero readiness must install an incomplete level-triggered ready signal");

        await client.StopAsync();
        var failure = await CaptureExceptionAsync(waiter);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "Stop must terminate a pending readiness waiter with the existing connection-closed taxonomy");
        Ensure(pendingPublication.Changed.Task.IsCompleted,
            "the Draining transition must complete the previous readiness generation");
        Ensure(client.ReadySignalForTesting.IsCompleted,
            "Stop must leave the ready signal permanently completed so terminal waiters cannot miss its pulse");
        Ensure(client.GetReadinessSnapshot() == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Stopped, 1, 0, 0, 1),
            "the fixed client must retain topology configuration while publishing terminal zero readiness");
    }

    [Test]
    public async Task StopAdmissionShouldRejectSatisfiedReadinessBeforeDrainingPublishes()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await client.ConnectAsync();
        client.CloseStopAdmissionForTesting();
        Ensure(client.GetReadinessSnapshot().State == SharpLinkConnectionState.Ready,
            "closing Stop admission alone must leave the pre-Draining publication observable");

        var waiter = client.WaitForReadinessAsync(1).AsTask();

        Ensure(!waiter.IsCompletedSuccessfully,
            "a satisfied fast or slow readiness path must not return Ready after Stop admission closes");
        var stop = client.StopAsync().AsTask();
        var failure = await CaptureExceptionAsync(waiter);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "the stop-racing readiness wait must terminate with the connection-closed taxonomy");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task StoppingDuringInitialConnectivityShouldMapOnlyInternalCancellationToConnectionClosed()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        var waiter = client.WaitForReadinessAsync(1).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = client.StopAsync().AsTask();
        var failure = await CaptureExceptionAsync(waiter);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "Client-owned shutdown cancellation during the joined ConnectAsync phase must use the readiness connection-closed taxonomy");
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkConnectionState.Stopped,
            "the mapped readiness failure must not interrupt the shared Stop operation");
    }

    [Test]
    public async Task ReadinessSnapshotGetterShouldAllocateZeroBytes()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        for (var index = 0; index < 100_000; index++)
            _ = client.GetReadinessSnapshot();

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var snapshot = client.GetReadinessSnapshot();
            checksum += snapshot.ActiveEndpoints + snapshot.TargetReadyEndpoints;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);

        Ensure(checksum == iterations * 2,
            "every getter call must observe the fixed Created publication");
        Ensure(allocated == 0,
            $"the lock-free readiness getter allocated {allocated} bytes over {iterations} calls");
    }

    [Test]
    public async Task PublicationShouldWakeAReaderThatCapturedThePreviousGeneration()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var previous = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(ReadyFacts);

        Ensure(previous.Changed.Task.IsCompleted,
            "publishing a new snapshot must complete the signal paired with the previous snapshot");
        await previous.Changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.ReadinessPublicationForTesting.Snapshot.ReadyConnections == 1,
            "a reader that awaits after publication must immediately observe the new generation");
    }

    [Test]
    public async Task PublicationShouldBeVisibleToReadersThatStartAfterTheChange()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());

        client.PublishReadinessFacts(ReadyFacts);
        var publication = client.ReadinessPublicationForTesting;

        Ensure(publication.Snapshot == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Created, 1, 1, 1, 1),
            "a reader starting after publication must directly read the new immutable snapshot");
        Ensure(!publication.Changed.Task.IsCompleted,
            "the current generation signal must remain pending until a later public change");
    }

    [Test]
    public async Task PublicationShouldWakeAnAlreadyAwaitingReader()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var previous = client.ReadinessPublicationForTesting;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = AwaitNextPublicationAsync(client, previous, entered);
        await entered.Task;
        Ensure(!waiter.IsCompleted,
            "the deterministic waiter must be suspended on the previous generation signal");

        client.PublishReadinessFacts(ReadyFacts);

        var observed = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(observed.ReadyEndpoints == 1 && observed.ReadyConnections == 1,
            "an already-awaiting reader must resume on the new publication");
    }

    [Test]
    public async Task BackToBackPublicationsShouldExposeTheLatestGenerationWithoutMissedWakeup()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var first = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(ReadyFacts);
        var second = client.ReadinessPublicationForTesting;
        client.PublishReadinessFacts(NotReadyFacts);
        var third = client.ReadinessPublicationForTesting;

        Ensure(first.Changed.Task.IsCompleted && second.Changed.Task.IsCompleted,
            "each replaced generation must release readers even when publishers run back-to-back");
        Ensure(!third.Changed.Task.IsCompleted,
            "the latest generation must own the next incomplete change signal");
        Ensure(ReferenceEquals(client.ReadinessPublicationForTesting, third) &&
               third.Snapshot.ReadyEndpoints == 0 && third.Snapshot.ReadyConnections == 0,
            "readers may skip intermediate generations but must converge on the latest snapshot");
    }

    [Test]
    public async Task PublishingIdenticalFactsShouldReuseTheCurrentGeneration()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var current = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(NotReadyFacts);

        Ensure(ReferenceEquals(current, client.ReadinessPublicationForTesting),
            "an identical public snapshot must not allocate or publish another generation");
        Ensure(!current.Changed.Task.IsCompleted,
            "an identical publication request must not wake readiness readers");
    }

    [Test]
    public async Task ReadinessPublicationShouldSurviveTenThousandConcurrentChanges()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishingComplete = new int[1];

        var observer = Task.Run(async () =>
        {
            await start.Task;
            while (true)
            {
                var publication = client.ReadinessPublicationForTesting;
                AssertStressSnapshot(publication.Snapshot);
                if (Volatile.Read(ref publishingComplete[0]) != 0 &&
                    publication.Snapshot.ReadyConnections == 0)
                {
                    return;
                }

                await publication.Changed.Task;
            }
        });
        var publisher = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 9_999; index++)
            {
                client.PublishReadinessFacts((index & 1) == 0 ? ReadyFacts : NotReadyFacts);
                if ((index & 63) == 0)
                    await Task.Yield();
            }

            Volatile.Write(ref publishingComplete[0], 1);
            client.PublishReadinessFacts(NotReadyFacts);
        });

        start.TrySetResult();
        await Task.WhenAll(observer, publisher).WaitAsync(TimeSpan.FromSeconds(10));

        var final = client.ReadinessPublicationForTesting.Snapshot;
        Ensure(final.ReadyEndpoints == 0 && final.ReadyConnections == 0,
            "the stress observer must converge on the tenth-thousand terminal publication");
    }

    private static readonly ClientReadinessFacts ReadyFacts = new(
        ActiveEndpoints: 1,
        ReadyEndpoints: 1,
        ReadyConnections: 1,
        TargetReadyEndpoints: 1);

    private static readonly ClientReadinessFacts NotReadyFacts = new(
        ActiveEndpoints: 1,
        ReadyEndpoints: 0,
        ReadyConnections: 0,
        TargetReadyEndpoints: 1);

    private static async Task<SharpLinkClientReadinessSnapshot> AwaitNextPublicationAsync(
        SharpLinkClient client,
        ClientReadinessPublication publication,
        TaskCompletionSource entered)
    {
        entered.TrySetResult();
        await publication.Changed.Task;
        return client.GetReadinessSnapshot();
    }

    private static async Task<SharpLinkClientReadinessSnapshot> WaitForReadinessSnapshotAsync(
        SharpLinkClient client,
        Func<SharpLinkClientReadinessSnapshot, bool> predicate)
    {
        while (true)
        {
            var publication = client.ReadinessPublicationForTesting;
            if (predicate(publication.Snapshot))
                return publication.Snapshot;
            await publication.Changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async ValueTask<TestTransportConnection> CreateReadyConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new TestTransportConnection();
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024));
        await connection.InjectFrameAsync(
            ProtocolV2FrameType.HandshakeResponse,
            ProtocolV2FrameFlags.None,
            0,
            payload.WrittenMemory,
            cancellationToken);
        return connection;
    }

    private static void AssertStressSnapshot(SharpLinkClientReadinessSnapshot snapshot)
    {
        Ensure(snapshot.State == SharpLinkConnectionState.Created,
            "fact-only stress publication must preserve the client lifecycle state");
        Ensure(snapshot.ActiveEndpoints == 1 && snapshot.TargetReadyEndpoints == 1,
            "stress publication must preserve fixed-topology configuration");
        Ensure(snapshot.ReadyEndpoints is 0 or 1 &&
               snapshot.ReadyConnections == snapshot.ReadyEndpoints,
            "stress publication must expose one complete valid fact set");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            return new Exception("expected the operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            return new Exception("expected the operation to fail");
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

    private sealed class BlockingInitialTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TestTransportConnection? _connection;
        private int _connectCount;

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);

            var connection = new TestTransportConnection();
            using var payload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            _connection = connection;
            return connection;
        }

        internal void ReleaseConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync()
            => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ControlledSequenceTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly bool _blockFirstAttempt;
        private readonly Exception? _firstFailure;
        private readonly bool _blockLaterAttempts;
        private readonly TaskCompletionSource _firstRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _laterRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;
        private int _blockDispose;

        internal ControlledSequenceTransportFactory(
            bool blockFirstAttempt = false,
            Exception? firstFailure = null,
            bool blockLaterAttempts = false)
        {
            _blockFirstAttempt = blockFirstAttempt;
            _firstFailure = firstFailure;
            _blockLaterAttempts = blockLaterAttempts;
        }

        internal TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource LaterAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<TestTransportConnection> FirstConnectionCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _connectCount);
            if (attempt == 1)
            {
                FirstAttemptStarted.TrySetResult();
                if (_blockFirstAttempt)
                    await _firstRelease.Task.WaitAsync(cancellationToken);
                if (_firstFailure is not null)
                    throw _firstFailure;
            }
            else
            {
                LaterAttemptStarted.TrySetResult();
                if (_blockLaterAttempts)
                    await _laterRelease.Task.WaitAsync(cancellationToken);
            }

            var connection = await CreateReadyConnectionAsync(cancellationToken);
            lock (_gate)
                _connections.Add(connection);
            if (attempt == 1)
                FirstConnectionCreated.TrySetResult(connection);
            return connection;
        }

        internal void ReleaseFirstAttempt() => _firstRelease.TrySetResult();

        internal void ReleaseLaterAttempts() => _laterRelease.TrySetResult();

        internal void BlockDispose() => Volatile.Write(ref _blockDispose, 1);

        internal void ReleaseDispose() => _disposeRelease.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            _firstRelease.TrySetResult();
            _laterRelease.TrySetResult();
            DisposeStarted.TrySetResult();
            if (Volatile.Read(ref _blockDispose) != 0)
                await _disposeRelease.Task;
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync();
        }
    }

    private sealed class FixedReadinessReconnectJitter(TimeSpan delay) : ISharpLinkReconnectJitter
    {
        public TimeSpan AddQuarterWindow(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }

        public TimeSpan ScaleTwentyPercent(int baseDelayMilliseconds)
        {
            _ = baseDelayMilliseconds;
            return delay;
        }
    }

    private sealed class LegacyThirdPartyClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Created;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkHealthCheckResult>(new NotSupportedException());

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();



        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService


            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
