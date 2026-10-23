using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming, 1, false)]
    [Arguments(RpcMethodKind.ClientStreaming, 3, false)]
    [Arguments(RpcMethodKind.ClientStreaming, 1, true)]
    [Arguments(RpcMethodKind.ClientStreaming, 3, true)]
    [Arguments(RpcMethodKind.OneWay, 1, true)]
    [Arguments(RpcMethodKind.OneWay, 3, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, 1, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, 3, false)]
    [Arguments(RpcMethodKind.DuplexStreaming, 1, true)]
    [Arguments(RpcMethodKind.DuplexStreaming, 3, true)]
    public async Task TerminalMustRetainOneReservationUntilEveryProducerDisposes(
        RpcMethodKind kind, int streamCount, bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = new ProducerGroup(streamCount, holdDispose: true);
        var invocation = Invoke(fixture, kind, probe);
        try
        {
            await probe.AllDisposing.WaitAsync(Bound);
            Ensure(fixture.Connection.CallAdmissionReservationCount == 2,
                "one outgoing group plus one unrelated selection must own exactly two reservations");
            if (cancel)
                await fixture.Cancellation.CancelAsync();
            else
                await fixture.CompleteReplyAsync(kind, probe.RequestId);
            await WaitUntilAsync(() => fixture.Connection.PendingCalls.Count == 0);
            Ensure(fixture.Connection.CallAdmissionReservationCount == 2,
                "removing pending capacity must not release physical producer ownership");
            if (kind == RpcMethodKind.ClientStreaming && !cancel)
                Ensure(await CaptureFailureAsync(invocation) is null, "early reply must remain successful");

            for (var index = 0; index < probe.Streams.Length - 1; index++)
            {
                probe.Streams[index].Release.TrySetResult();
                await probe.Tasks[index].WaitAsync(Bound);
                Ensure(fixture.Connection.CallAdmissionReservationCount == 2,
                    "a completed member must not release the group's single reservation");
            }
            probe.ReleaseAll();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "group completion must release exactly its own reservation, not the unrelated one");
            probe.AssertCounts();
        }
        finally
        {
            probe.ReleaseAll();
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }

    [Test]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task FaultedMemberMustNotReleaseReservationWhileAnotherMemberDisposes(RpcMethodKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = new ProducerGroup(3, "move-sync", holdDispose: true);
        var invocation = Invoke(fixture, kind, probe);
        try
        {
            await probe.AllDisposing.WaitAsync(Bound);
            Ensure(probe.Tasks[0].IsFaulted && !probe.Tasks[1].IsCompleted && !probe.Tasks[2].IsCompleted,
                "the first real stream must fault while both other real disposers remain held");
            await fixture.Cancellation.CancelAsync();
            Ensure(fixture.Connection.PendingCalls.Count == 0 && fixture.Connection.CallAdmissionReservationCount == 2,
                "a member fault and pending cancellation cannot release group ownership");
            probe.Streams[1].Release.TrySetResult();
            await probe.Tasks[1].WaitAsync(Bound);
            Ensure(fixture.Connection.CallAdmissionReservationCount == 2,
                "the final held member still owns the group reservation after another member finishes");
            probe.ReleaseAll();
            Ensure(await CaptureFailureAsync(invocation) is not null, "cancellation or producer fault must win");
            await fixture.JoinProducersAsync();
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1, "exactly the unrelated reservation remains");
            probe.AssertCounts();
        }
        finally
        {
            probe.ReleaseAll();
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }
}
