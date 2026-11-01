using SharpLink.UnitTests.Runtime;

namespace SharpLink.Type22Type23TcpTests;

[NotInParallel]
public sealed partial class TcpAcceptanceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedFiveFamiliesAndManualTwoStreamOneWayUseRealTcp(bool interceptor)
    {
        await using var harness = new Harness(interceptor);
        var proxy = await harness.PrepareAsync();
        var service = proxy.Service;
        Ensure(await service.UnaryAsync(7, default) == 8, "unary value");
        await service.OneWayAsync(11, default);
        Ensure(await harness.Service.Notification.Task.WaitAsync(Budget) == 11, "plain one-way business");
        Ensure(await service.ClientStreamingAsync(new Producer(), default) == 0, "empty client stream");
        Ensure(await service.ClientStreamingAsync(new Producer(2, 3), default) == 5, "client stream values");
        Ensure(await service.ClientStreamingWithSeedAsync(10, new Producer(2, 3), default) == 15, "seed payload");
        Ensure((await CollectAsync(service.ServerStreamingAsync(4, default))).SequenceEqual(new[] { 4, 5 }), "server stream");
        Ensure((await CollectAsync(service.DuplexStreamingAsync(new Producer(), default))).Length == 0, "empty duplex");
        Ensure((await CollectAsync(service.DuplexStreamingAsync(new Producer(2, 3), default))).SequenceEqual(new[] { 4, 6 }), "duplex");
        Ensure((await CollectAsync(service.DuplexStreamingWithSeedAsync(10, new Producer(2, 3), default))).SequenceEqual(new[] { 14, 16 }), "seed duplex");
        await harness.AssertQuiescentAsync();
        Ensure(harness.Service.Calls == 9 && harness.Accepted == 6,
            "nine business calls; only three client-stream plus three duplex heads receive type22");

        var channel = harness.Client.CreateContractChannel(harness.Client.GetLocalContract<ITcpAcceptanceService>());
        await channel.PrepareAsync(Budget);
        var first = new Producer(1, 2);
        var second = new Producer(3, 4);
        var probe = new WriterProbe();
        var writer = new PairWriter(first, second, channel.RuntimeContext.Codecs.GetCodec<int>(), probe);
        await channel.InvokeOneWayAsync(PairDescriptor(service), default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, writer, null);
        Ensure(await harness.Service.PairResult.Task.WaitAsync(Budget) == 10, "manual real declared pair values");
        await harness.AssertQuiescentAsync();
        Ensure(probe.Entries == 1 && first.Disposed == 1 && second.Disposed == 1, "one writer and exact physical producer exits");
        Ensure(harness.Service.Calls == 10 && harness.Accepted == 6, "type23 does not receive type22");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CompleteRouteReservationStillCannotStartGeneratedProducerBeforeAck(bool duplex, bool interceptor)
    {
        await using var harness = new Harness(interceptor);
        var proxy = await harness.PrepareAsync();
        var reserved = NewGate();
        var release = NewGate();
        harness.Server.BeforeRequestAcceptedForTesting = (_, _) =>
        { reserved.TrySetResult(); release.Task.WaitAsync(Budget).GetAwaiter().GetResult(); };
        var producer = new Producer(2, 3);
        Task call = duplex ? CollectAsync(proxy.Service.DuplexStreamingAsync(producer, default))
            : proxy.Service.ClientStreamingAsync(producer, default).AsTask();
        try
        {
            await reserved.Task.WaitAsync(Budget);
            Ensure(harness.Connection.Session.StreamManager.ActiveStreamCount == 1, "real reserved incoming route");
            Ensure(harness.Accepted == 0 && producer.Enumerators == 0 && producer.Moves == 0,
                "no producer before type22 acceptance with zero or pass-through server interceptors");
            Ensure(harness.Service.Calls == 0, "server business entry remains zero before type22 acceptance");
        }
        finally { release.TrySetResult(); }
        await call.WaitAsync(Budget);
        await harness.AssertQuiescentAsync();
        Ensure(harness.Accepted == 1 && producer.Enumerators == 1 && producer.Disposed == 1, "one ACK and producer lifecycle");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginalCancellationOrUnfiredDeadlineBeforeAckDoesNotStartProducer(bool deadline)
    {
        var clock = deadline ? new ManualTimeProvider() : null;
        await using var harness = new Harness(clock: clock);
        var proxy = await harness.PrepareAsync();
        var connection = CaptureOnlyReadyConnection(harness.Client);
        var clientSession = connection.Session;
        var serverSession = harness.Connection.Session;
        var reserved = NewGate();
        var release = NewGate();
        harness.Server.BeforeRequestAcceptedForTesting = (_, _) =>
        { reserved.TrySetResult(); release.Task.WaitAsync(Budget).GetAwaiter().GetResult(); };
        using var cancellation = new CancellationTokenSource();
        var producer = new Producer(1);
        var previousContext = SharpLinkCallContext.Current;
        var originalDeadline = default(RpcDeadline);
        Task<int> call;
        if (deadline)
        {
            // Streaming deliberately excludes client-wide fallback. Supply a genuine
            // existing parent deadline, frozen only at this generated call's creation.
            originalDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock!);
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                       "type22-tcp-original-parent", authentication: null, originalDeadline, clock!)))
            {
                call = proxy.Service.ClientStreamingAsync(producer, cancellation.Token).AsTask();
            }
        }
        else call = proxy.Service.ClientStreamingAsync(producer, cancellation.Token).AsTask();
        Ensure(ReferenceEquals(SharpLinkCallContext.Current, previousContext),
            "inherited deadline scope ends before awaits, clock advance and later healthy unary");
        try
        {
            await reserved.Task.WaitAsync(Budget);
            if (deadline)
            {
                clock!.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(2));
                Ensure(originalDeadline.IsExpired(clock), "same original inherited deadline expired without timer callbacks");
            }
            else
            {
                cancellation.Cancel();
                await ExpectTerminalAsync(call, SharpLinkErrorCode.Cancelled);
            }
            Ensure(producer.Enumerators == 0 && producer.Moves == 0, "original terminal precedes producer start");
        }
        finally { release.TrySetResult(); }
        await ExpectTerminalAsync(call, deadline ? SharpLinkErrorCode.DeadlineExceeded : SharpLinkErrorCode.Cancelled);
        await harness.AssertQuiescentAsync();
        Ensure(producer.Enumerators == 0 && producer.Moves == 0, "late acceptance cannot resurrect terminal producer");
        if (deadline) Ensure(harness.Accepted == 0 && harness.Service.Calls == 0, "server samples original expired deadline without timer firing");
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
        Ensure(await proxy.Service.UnaryAsync(5, default) == 6, "same healthy session remains useful");
        await harness.AssertQuiescentAsync();
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
    }

    [Test]
    public async Task Type23EarlyReturnDiscardsBeyondOneWindowWithoutAcceptanceAck()
    {
        await using var harness = new Harness();
        harness.Service.ReturnWithoutReadingPair = true;
        var proxy = await harness.PrepareAsync();
        var connection = CaptureOnlyReadyConnection(harness.Client);
        var clientSession = connection.Session;
        var serverSession = harness.Connection.Session;
        var channel = harness.Client.CreateContractChannel(harness.Client.GetLocalContract<ITcpAcceptanceService>());
        await channel.PrepareAsync(Budget);
        var first = new Producer(Enumerable.Repeat(1, 256).ToArray());
        var second = new Producer(Enumerable.Repeat(2, 256).ToArray());
        var probe = new WriterProbe();
        var writer = new PairWriter(first, second, channel.RuntimeContext.Codecs.GetCodec<int>(), probe);
        Ensure(harness.Connection.Session.NegotiatedOptions!.StreamReceiveWindowBytes == 128, "small negotiated real window");
        await channel.InvokeOneWayAsync(PairDescriptor(proxy.Service), default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, writer, null);
        Ensure(await harness.Service.PairResult.Task.WaitAsync(Budget) == -1, "handler deliberately returns without enumeration");
        await harness.AssertQuiescentAsync();
        Ensure(first.Moves == 257 && second.Moves == 257 && first.Disposed == 1 && second.Disposed == 1,
            "each real producer crossed its receive window and physically terminated");
        Ensure(harness.Service.PairItems == 0 && harness.Accepted == 0, "discard does not invoke handler enumeration or ACK");
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
        Ensure(await proxy.Service.UnaryAsync(8, default) == 9, "discard preserves TCP progress");
        await harness.AssertQuiescentAsync();
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
    }

    [Test]
    public async Task CancelledAcceptedCallCleansPeerRoutesWhileRealMoveNextRemainsHeld()
    {
        await using var harness = new Harness();
        var proxy = await harness.PrepareAsync();
        var connection = CaptureOnlyReadyConnection(harness.Client);
        var clientSession = connection.Session;
        var serverSession = harness.Connection.Session;
        using var cancellation = new CancellationTokenSource();
        var producer = new Producer(1) { HoldMove = true };
        var call = proxy.Service.ClientStreamingAsync(producer, cancellation.Token).AsTask();
        try
        {
            await producer.MoveEntered.Task.WaitAsync(Budget);
            await WaitUntilAsync(() => harness.Accepted == 1, "server acceptance observation did not finish");
            cancellation.Cancel();
            await ExpectTerminalAsync(call, SharpLinkErrorCode.Cancelled);
            await WaitUntilAsync(() => serverSession.StreamManager.ActiveStreamCount == 0,
                "independent host error terminals did not retire peer routes while user tail was held");
            Ensure(producer.Disposed == 0 && harness.Client.ActiveClientCallCount > 0,
                "logical completion and peer cleanup cannot release held physical producer/pending owner");
            AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
        }
        finally { producer.ReleaseMove.TrySetResult(); }
        await harness.AssertQuiescentAsync();
        Ensure(producer.TokenOwnerLiveAfterHold && producer.Disposed == 1, "original token owner survives through actual MoveNext exit");
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
        Ensure(await proxy.Service.UnaryAsync(9, default) == 10, "healthy connection after final tail join");
        await harness.AssertQuiescentAsync();
        AssertOriginalTcpSessions(harness, connection, clientSession, serverSession);
    }

    private static void AssertOriginalTcpSessions(Harness harness, ClientConnection connection,
        RpcSession clientSession, RpcSession serverSession)
    {
        Ensure(ReferenceEquals(connection, CaptureOnlyReadyConnection(harness.Client)) &&
            ReferenceEquals(clientSession, connection.Session) &&
            ReferenceEquals(serverSession, harness.Connection.Session),
            "the originally prepared TCP connection and both Sessions must remain exact; reconnect cannot prove cleanup");
        Ensure(clientSession.IsConnected && serverSession.IsConnected,
            "ordinary bounded cleanup must preserve both original TCP Sessions before and after unary progress");
    }

}
