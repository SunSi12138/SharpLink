namespace SharpLink.UnitTests.Runtime;

public partial class StreamFlowGrantGeneratedBridgeTests
{
    [Test]
    public async Task DeadlineAfterAdmissionMustLetItsAbandonedItemRefundAcrossTheOwnerErrorTerminal()
    {
        await using var harness = new GrantBridgeHarness();
        var time = new ManualTimeProvider();
        var stream = new NonCooperativeValues([1, 2], blockEof: false);
        var serializeEntered = NewSignal();
        using var releaseSerialize = new ManualResetEventSlim();
        var codec = new GrantProbeCodec(encodedSize: value => value == 2 ? 64 : 4)
        {
            BeforeSerialize = (value, _) =>
            {
                if (value != 2)
                    return;
                serializeEntered.TrySetResult();
                Ensure(releaseSerialize.Wait(BridgeTimeout),
                    "fixture must release the noncooperative serializer after observing deadline ownership");
            }
        };
        Task? pump = null;
        try
        {
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                       harness.Session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = harness.PumpAsync(stream, codec);
            }
            Ensure(!pump.IsCompleted && harness.Snapshot.WaiterCount == 1 && codec.SizedCalls == 1,
                "the second exact item must asynchronously wait before entering its serializer");
            harness.ReturnPeerCredit(4);
            await serializeEntered.Task.WaitAsync(BridgeTimeout);
            harness.AssertCredit(available: 0, debt: 64, grant: 0);
            Ensure(!IsInactive(codec.LastWriter), "the serializer must still own its admitted frame");

            time.Advance(TimeSpan.FromSeconds(1));
            var failure = await CaptureFailureAsync(pump);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the frozen deadline must win while admitted user serialization is still blocked");
            Ensure(stream.DisposeRequested && !stream.DisposeCompleted && stream.Token.IsCancellationRequested,
                "noncooperative user disposal must be observed without holding the call open");
            Ensure(harness.Snapshot.LeaseClosed,
                "the immutable lease must stop accepting items before deferred disposal finishes");
            harness.AssertCredit(available: 0, debt: 64, grant: 0);

            // This is the real server-owner ordering: the RPC reports its error before an
            // abandoned item helper can settle. Delaying this terminal would hide the race.
            harness.PublishError(SharpLinkErrorCode.DeadlineExceeded);
            Ensure(harness.Snapshot.Completed && harness.Snapshot.RetainedStreams == 1,
                "the terminal must retain the generation while an admitted unsent frame still owns credit");
            releaseSerialize.Set();
            await WaitUntilAsync(() => codec.ReleaseCalls == 2 && IsInactive(codec.LastWriter),
                "the abandoned helper must eventually release its snapshot and writer");
            harness.AssertFullyRetired();
            Ensure(harness.Session.IsConnected, "a stream deadline must not terminate the session");
            AssertFrames(await harness.ReadFramesAsync(), [1], errorTerminal: true);
        }
        finally
        {
            releaseSerialize.Set();
            stream.Release();
            if (pump is not null)
                await ObserveFixtureTaskAsync(pump);
            if (serializeEntered.Task.IsCompletedSuccessfully)
                await WaitUntilAsync(() => codec.ReleaseCalls == 2 && IsInactive(codec.LastWriter),
                    "fixture cleanup must settle the released noncooperative serializer");
        }
    }

    [Test]
    public async Task DeadlineMustReturnUnusedGrantBeforeNoncooperativeMoveNextAndDisposeFinish()
    {
        await using var harness = new GrantBridgeHarness();
        var time = new ManualTimeProvider();
        var stream = new NonCooperativeValues([1, 2], blockEof: true);
        var codec = new GrantProbeCodec();
        Task? pump = null;
        try
        {
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                       harness.Session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), time), time)))
            {
                pump = harness.PumpAsync(stream, codec);
            }
            await stream.WaitingForMoveNext.Task.WaitAsync(BridgeTimeout);
            Ensure(!pump.IsCompleted && harness.Snapshot.UnusedGrant > 0,
                "the suspended producer must hold a real unused refill before its deadline");
            harness.AssertCredit(available: 0, debt: 8, grant: 56);

            time.Advance(TimeSpan.FromSeconds(1));
            var failure = await CaptureFailureAsync(pump);
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the deadline must return without cooperation from MoveNext or Dispose");
            Ensure(stream.DisposeRequested && !stream.DisposeCompleted && stream.Token.IsCancellationRequested,
                "the lifetime must cancel and initiate disposal before handing failure to the owner");
            Ensure(codec.ReleaseCalls == 2 && harness.Snapshot.LeaseClosed,
                "only the two published snapshots may be consumed before closing the grant lease");
            harness.AssertCredit(available: 56, debt: 8, grant: 0);
            harness.AssertNoPendingOwner();

            harness.PublishError(SharpLinkErrorCode.DeadlineExceeded);
            harness.ReturnPeerCredit(8);
            harness.AssertFullyRetired();
            Ensure(harness.Session.IsConnected, "a noncooperative stream must not take down its session");
            AssertFrames(await harness.ReadFramesAsync(), [1, 2], errorTerminal: true);
        }
        finally
        {
            stream.Release();
            if (pump is not null)
                await ObserveFixtureTaskAsync(pump);
        }
    }
}
