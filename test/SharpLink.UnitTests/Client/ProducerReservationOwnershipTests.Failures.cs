using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming, "request")]
    [Arguments(RpcMethodKind.ClientStreaming, "writer-sync")]
    [Arguments(RpcMethodKind.ClientStreaming, "writer-async")]
    [Arguments(RpcMethodKind.ClientStreaming, "enumerator")]
    [Arguments(RpcMethodKind.ClientStreaming, "move-sync")]
    [Arguments(RpcMethodKind.ClientStreaming, "move-async")]
    [Arguments(RpcMethodKind.ClientStreaming, "dispose-sync")]
    [Arguments(RpcMethodKind.ClientStreaming, "dispose-async")]
    [Arguments(RpcMethodKind.OneWay, "request")]
    [Arguments(RpcMethodKind.OneWay, "writer-sync")]
    [Arguments(RpcMethodKind.OneWay, "writer-async")]
    [Arguments(RpcMethodKind.OneWay, "enumerator")]
    [Arguments(RpcMethodKind.OneWay, "move-sync")]
    [Arguments(RpcMethodKind.OneWay, "move-async")]
    [Arguments(RpcMethodKind.OneWay, "dispose-sync")]
    [Arguments(RpcMethodKind.OneWay, "dispose-async")]
    [Arguments(RpcMethodKind.DuplexStreaming, "request")]
    [Arguments(RpcMethodKind.DuplexStreaming, "writer-sync")]
    [Arguments(RpcMethodKind.DuplexStreaming, "writer-async")]
    [Arguments(RpcMethodKind.DuplexStreaming, "enumerator")]
    [Arguments(RpcMethodKind.DuplexStreaming, "move-sync")]
    [Arguments(RpcMethodKind.DuplexStreaming, "move-async")]
    [Arguments(RpcMethodKind.DuplexStreaming, "dispose-sync")]
    [Arguments(RpcMethodKind.DuplexStreaming, "dispose-async")]
    public async Task StartupAndProducerFaultsReleaseOnlyTheirOwnReservation(RpcMethodKind kind, string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        var group = new ProducerGroup(1, fault);
        var codec = fault == "request" ? new ObservingCodec(() => throw group.Failure) : null;
        var invocation = Invoke(fixture, kind, group, codec);
        try
        {
            Ensure(await CaptureFailureAsync(invocation) is not null, $"{kind}/{fault} must fail");
            await fixture.JoinProducersAsync();
            Ensure(group.Calls == (fault == "request" ? 0 : 1), "startup faults must not invent a producer");
            Ensure(fixture.Connection.PendingCalls.Count == 0 && fixture.Connection.ActiveCallCount == 0,
                "failed invocation must release its pending and active-call capacity");
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "fault cleanup must release exactly one reservation and preserve the unrelated selection");
            var stream = group.Streams[0];
            var expectedEnumerations = fault is "request" or "writer-sync" or "writer-async" ? 0 : 1;
            Ensure(stream.Enumerations == expectedEnumerations, "enumerator construction must run exactly as far as the fault permits");
            Ensure(stream.Disposals == (expectedEnumerations == 1 && fault != "enumerator" ? 1 : 0),
                "constructed enumerator must dispose exactly once, even after a synchronous MoveNext failure");
        }
        finally
        {
            group.ReleaseAll();
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    public async Task ZeroOutgoingStreamsTransferBeforeRequestSerialization(RpcMethodKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var group = new ProducerGroup(0);
        var calls = 0;
        var reservationsDuringSerialization = -1;
        var codec = new ObservingCodec(() =>
        {
            calls++;
            reservationsDuringSerialization = fixture.Connection.CallAdmissionReservationCount;
            throw group.Failure;
        });
        var invocation = Invoke(fixture, kind, group, codec);
        Ensure(await CaptureFailureAsync(invocation) is not null, "the injected serialization fault must win");
        await fixture.JoinProducersAsync();
        Ensure(reservationsDuringSerialization == 1,
            "nonproducer serialization must not retain its own selection reservation");
        Ensure(calls == 1 && group.Calls == 0, "zero-stream shapes serialize once and never enter the writer");
        Ensure(fixture.Connection.CallAdmissionReservationCount == 1 && fixture.Connection.ActiveCallCount == 0,
            "nonproducer cleanup must preserve the unrelated selection");
    }

    [Test]
    public async Task DirectPendingRegistrationCannotConsumeAnotherSelectionReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var pending = fixture.Connection.PendingCalls.RegisterOneWayClientStream(default, default);
        var result = pending.Operation.AsValueTask();
        try
        {
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "a direct pending-table registration did not acquire the existing unrelated selection");
        }
        finally
        {
            fixture.Connection.PendingCalls.TryComplete(pending.Id, PendingCallCompletionReason.LocalStreamComplete);
            _ = await result;
        }
        Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
            "pending completion cannot release a selection it never acquired");
    }

    private sealed class ObservingCodec(Action serialize) : IRpcCodec<RpcEmptyRequest>
    {
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> writer) => serialize();
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
