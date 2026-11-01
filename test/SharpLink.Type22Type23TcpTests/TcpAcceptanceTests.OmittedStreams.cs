using System.Reflection;

namespace SharpLink.Type22Type23TcpTests;

public sealed partial class TcpAcceptanceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ManualDeclaredPairOmissionErrorsPeerAndReleasesActualOwnersWithoutClosingTcp(bool supplyFirst)
    {
        await using var harness = new Harness();
        var proxy = await harness.PrepareAsync();
        var connection = CaptureOnlyReadyConnection(harness.Client);
        var serverSession = harness.Connection.Session;
        Ensure(connection.PendingCalls.Count == 0 && connection.PendingCalls.ActiveCount == 0 &&
            connection.CallAdmissionReservationCount == 0, "exact original client owner baseline");
        var channel = harness.Client.CreateContractChannel(harness.Client.GetLocalContract<ITcpAcceptanceService>());
        await channel.PrepareAsync(Budget);
        var producer = new Producer(3, 4);
        var probe = new OmissionProbe();
        var writer = new OmittedPairWriter(supplyFirst, producer, channel.RuntimeContext.Codecs.GetCodec<int>(), probe);
        // No method/count fabrication: same generated pair descriptor used by the positive type23 control.
        var invocation = channel.InvokeOneWayAsync(PairDescriptor(proxy.Service), default(RpcEmptyRequest),
            RpcEmptyRequestCodec.Instance, writer, null).AsTask();
        Exception? terminal = null;
        try { await invocation.WaitAsync(Budget); }
        catch (Exception error) { terminal = error; }
        AssertExactOmittedStreamFailure(terminal);
        await harness.Service.PairEntered.Task.WaitAsync(Budget);
        var peerError = await harness.Service.PairFailure.Task.WaitAsync(Budget);
        Ensure(peerError is SharpLinkException { Code: SharpLinkErrorCode.Cancelled } &&
            peerError.Message == "Client stream terminated before every declared stream completed.",
            "generated peer enumerator receives the actual framework StreamComplete(Error), not a timeout or close");
        await harness.Service.PairExited.Task.WaitAsync(Budget);
        Ensure(!harness.Service.PairResult.Task.IsCompleted && harness.Service.Calls == 1,
            "real generated peer handler observes failure instead of fabricated clean completion");
        await harness.AssertQuiescentAsync();
        await WaitUntilAsync(() => connection.PendingCalls.Count == 0 && connection.PendingCalls.ActiveCount == 0 &&
            connection.CallAdmissionReservationCount == 0 && connection.Session.QueuedSendBytes == 0,
            "exact original pending capacity, selection reservation or outbound frames remained owned");
        Ensure(probe.WriterEntries == 1 && probe.SinkCalls == (supplyFirst ? 1 : 0),
            "one real writer, with exactly zero or one actual sink registration");
        Ensure(producer.Enumerators == (supplyFirst ? 1 : 0) && producer.Moves == (supplyFirst ? 3 : 0) &&
            producer.Disposed == (supplyFirst ? 1 : 0), "exact zero/one real producer lifecycle, no invented omitted producer");
        Ensure(harness.Accepted == 0 && serverSession.IsConnected && connection.Session.IsConnected,
            "type23 has no ACK and ordinary bounded omission cleanup preserves both exact TCP Sessions");
        Ensure(ReferenceEquals(connection, CaptureOnlyReadyConnection(harness.Client)), "no reconnect substitutes for cleanup");
        Ensure(await proxy.Service.UnaryAsync(21, default) == 22, "same receiver/session makes real unary progress after omission");
        await harness.AssertQuiescentAsync();
        Ensure(ReferenceEquals(serverSession, harness.Connection.Session) &&
            ReferenceEquals(connection, CaptureOnlyReadyConnection(harness.Client)), "post-progress identity still exact");
    }

    private sealed class OmissionProbe { internal int WriterEntries, SinkCalls; }
    private readonly struct OmittedPairWriter(bool supplyFirst, Producer first, IRpcCodec<int> codec, OmissionProbe probe)
        : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken token)
        {
            Interlocked.Increment(ref probe.WriterEntries);
            if (supplyFirst)
            {
                Interlocked.Increment(ref probe.SinkCalls);
                await sink.SendClientStreamAsync(requestId, 1, first, codec, token);
            }
            // Intentionally no stream 2 registration; the zero variant also omits stream 1.
            // Returning is the bug being exercised, not a fabricated terminal or a fake producer.
        }
    }

    private static void AssertExactOmittedStreamFailure(Exception? error)
    {
        // FinishAsync selects this exact error on the pending operation before its later
        // physical cleanup aggregation. Neither controlled writer intentionally throws.
        // Reject wrappers and every extra failure; retain the complete unexpected exception.
        if (error is null || error.GetType() != typeof(InvalidOperationException) ||
            error.Message != "The client stream writer omitted a declared stream." || error.InnerException is not null)
            throw new InvalidOperationException(
                "Expected only the exact omitted-stream writer failure, never success, a wrapper or sibling cleanup failure.", error);
    }

    private static ClientConnection CaptureOnlyReadyConnection(SharpLinkClient client)
    {
        // Same read-only real-selection observation used by ClientStreamProducerDeadlineTests.
        // Never populate a Ready array or construct a synthetic connection.
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField("_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!);
        Ensure(connections.Length == 1, "exactly one established real TCP client connection");
        return connections[0];
    }
}
