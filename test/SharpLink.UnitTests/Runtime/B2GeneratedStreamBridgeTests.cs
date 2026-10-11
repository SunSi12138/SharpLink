namespace SharpLink.UnitTests.Runtime;

// These tests exercise the concrete generated-server bridge with a test-only sized codec.
// The benchmark byte[] codec is not IRpcSizedCodec and cannot establish B2 activation.
public partial class StreamFlowGrantGeneratedBridgeTests
{
    [Test]
    public async Task FirstItemMustSelectOnePathAndTransferItsSnapshotAndExactReservationOnce()
    {
        foreach (var firstIsSized in new[] { true, false })
        {
            await using var harness = new GrantBridgeHarness();
            var observations = new List<StreamFlowController.SendGrantSnapshot>();
            var codec = new GrantProbeCodec(value => firstIsSized || value != 1)
            {
                BeforeSerialize = (_, _) => observations.Add(harness.Snapshot)
            };
            await harness.PumpAsync(new ImmediateValues(1, 2), codec).WaitAsync(BridgeTimeout);

            Ensure(observations.Count == 2, "each item must serialize exactly once");
            if (firstIsSized)
            {
                Ensure(observations[0].CurrentLease is not null &&
                       observations[0].ConnectionCredit == 60 && observations[0].UnusedGrant == 0,
                    "the first selected item must own exactly four bytes, without speculative grant");
                Ensure(ReferenceEquals(observations[0].CurrentLease, observations[1].CurrentLease) &&
                       observations[0].Lease == observations[1].Lease && observations[1].UnusedGrant > 0,
                    "the next sized item must refill on the same immutable lease");
                Ensure(codec.SizedCalls == 2 && codec.UnsizedCalls == 0 && codec.ReleaseCalls == 2,
                    "both captured snapshots must be serialized and released exactly once");
            }
            else
            {
                Ensure(observations.TrueForAll(static state =>
                           state.CurrentLease is null && state.UnusedGrant == 0),
                    "a first-item sizing miss must keep the entire stream on the keyed fallback");
                Ensure(codec.SizedCalls == 1 && codec.UnsizedCalls == 1 && codec.ReleaseCalls == 1,
                    "later sizing success may use its snapshot but must not silently activate a lease");
            }

            harness.AssertCredit(available: 56, debt: 8, grant: 0);
            Ensure(harness.Snapshot.ActiveStreams == 0,
                "successful EOF must retire the logical stream while retaining its actual sent debt");
            harness.ReturnPeerCredit(8);
            harness.AssertFullyRetired();
            AssertFrames(await harness.ReadFramesAsync(), [1, 2], errorTerminal: false);
        }

        // The snapshot exists before the new admission probe. Its failing release must not
        // replace the cancellation selected by that probe before the item helper owns it.
        await using var canceledHarness = new GrantBridgeHarness();
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = new InvalidOperationException("first snapshot cleanup failed");
        var canceledCodec = new GrantProbeCodec
        {
            BeforeSize = _ => cancellation.Cancel(),
            ReleaseFailure = cleanupFailure
        };
        var canceledStream = new ImmediateValues(1);
        var failure = await CaptureFailureAsync(canceledHarness.PumpAsync(
            canceledStream, canceledCodec, cancellation.Token));
        Ensure(failure is OperationCanceledException canceled &&
               canceled.CancellationToken == canceledStream.Token,
            "first admission cancellation must survive a secondary snapshot release failure");
        Ensure(canceledCodec.ReleaseCalls == 1 && canceledCodec.LastWriter is null,
            "the untransferred snapshot must be released once without allocating an item writer");
        canceledHarness.AssertFullyRetired();
    }

    [Test]
    public async Task MidStreamSizingMissCancellationMustReleasePreparedBudgetAndKeepTheLeaseGeneration()
    {
        await using var harness = new GrantBridgeHarness();
        using var cancellation = new CancellationTokenSource();
        StreamFlowController.SendGrantSnapshot sized = default, unsized = default;
        var codec = new GrantProbeCodec(value => value < 3, value => value == 3 ? 64 : 4)
        {
            BeforeSerialize = (value, _) =>
            {
                if (value == 2)
                    sized = harness.Snapshot;
                if (value == 3)
                    unsized = harness.Snapshot;
            }
        };
        var pump = harness.PumpAsync(new ImmediateValues(1, 2, 3), codec, cancellation.Token);
        Ensure(!pump.IsCompleted && harness.Snapshot.WaiterCount == 1,
            "the serialized third item must wait for its missing actual flow credit");
        Ensure(sized.UnusedGrant > 0 && ReferenceEquals(sized.CurrentLease, unsized.CurrentLease) &&
               sized.Lease == unsized.Lease && !unsized.GrantEnabled && unsized.UnusedGrant == 0,
            "sizing fallback must return the real grant while preserving the generation for exact admission");
        Ensure(harness.Session.PreCreditSerializedBytes == 64 &&
               harness.Session.PreCreditSerializedWaiterCount == 0,
            "the blocked unsized frame must be the sole admitted pre-credit byte owner");

        cancellation.Cancel();
        var failure = await CaptureFailureAsync(pump);
        Ensure(failure is OperationCanceledException,
            "user cancellation must propagate through the generated pump");
        Ensure(codec.ReleaseCalls == 2 && IsInactive(codec.LastWriter),
            "cancellation must release both snapshots and the never-published unsized writer");
        harness.AssertCredit(available: 56, debt: 8, grant: 0);
        harness.AssertNoPendingOwner();
        Ensure(harness.Session.IsConnected, "canceling one stream must not fail its session");

        harness.PublishError();
        harness.ReturnPeerCredit(8);
        harness.AssertFullyRetired();
        AssertFrames(await harness.ReadFramesAsync(), [1, 2], errorTerminal: true);
    }

    [Test]
    public async Task SizedSerializerFailureAfterRefillMustPreserveTheFailureAndRefundOnlyUnpublishedBytes()
    {
        await using var harness = new GrantBridgeHarness();
        var original = new InvalidOperationException("second sized item serialization failed");
        StreamFlowController.SendGrantSnapshot refilled = default;
        var codec = new GrantProbeCodec
        {
            AfterSerialize = (value, _) =>
            {
                if (value != 2)
                    return;
                refilled = harness.Snapshot;
                throw original;
            }
        };
        var failure = await CaptureFailureAsync(harness.PumpAsync(new ImmediateValues(1, 2), codec));

        Ensure(ReferenceEquals(failure, original), "the serializer's original exception must reach its owner");
        Ensure(refilled.CurrentLease is not null && refilled.UnusedGrant > 0,
            "this failure must occur after a real grant refill, not on the legacy fallback");
        Ensure(codec.ReleaseCalls == 2 && IsInactive(codec.LastWriter),
            "the failed partially written item must release its unique snapshot and writer");
        harness.AssertCredit(available: 60, debt: 4, grant: 0);
        harness.AssertNoPendingOwner();

        harness.PublishError();
        harness.ReturnPeerCredit(4);
        harness.AssertFullyRetired();
        AssertFrames(await harness.ReadFramesAsync(), [1], errorTerminal: true);
    }

    [Test]
    public async Task RejectedUnsizedPacketAfterCreditHandoffMustReturnItsBudgetWriterAndExactLeaseCredit()
    {
        const int payloadBytes = 30 * 1024;
        await using var harness = new GrantBridgeHarness(payloadBytes, sendQueueBytes: 32 * 1024);
        StreamFlowController.SendGrantSnapshot refilled = default, fallback = default;
        var codec = new GrantProbeCodec(value => value < 3, value => value == 3 ? payloadBytes : 4)
        {
            BeforeSerialize = (value, _) =>
            {
                if (value == 2)
                    refilled = harness.Snapshot;
                if (value == 3)
                    fallback = harness.Snapshot;
            }
        };
        var pump = harness.PumpAsync(new ImmediateValues(1, 2, 3), codec);
        Ensure(!pump.IsCompleted && harness.Snapshot.WaiterCount == 1 &&
               harness.Session.PreCreditSerializedBytes == payloadBytes,
            "the oversized-for-DATA-queue frame must first acquire the real pre-credit budget");
        Ensure(refilled.UnusedGrant > 0 && ReferenceEquals(refilled.CurrentLease, fallback.CurrentLease) &&
               refilled.Lease == fallback.Lease && !fallback.GrantEnabled && fallback.UnusedGrant == 0,
            "the pending handoff must use the same disabled immutable lease after returning its grant");

        // The existing 32 KiB queue reserves 4 KiB for progress. A 30 KiB DATA frame
        // cannot fit even when empty; no timing assumption about SendPump is required.
        harness.ReturnPeerCredit(8);
        var failure = await CaptureFailureAsync(pump);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted } &&
               failure.Message.Contains("send_queue_capacity", StringComparison.Ordinal),
            "the packet must fail at the established SendPump capacity boundary after admission");
        Ensure(codec.ReleaseCalls == 2 && IsInactive(codec.LastWriter),
            "SendPacket must return the rejected writer once after the helper transfers ownership");
        harness.AssertCredit(available: payloadBytes, debt: 0, grant: 0);
        harness.AssertNoPendingOwner();
        Ensure(harness.Session.IsConnected, "a bounded DATA rejection must leave the connection live");

        harness.PublishError();
        harness.AssertFullyRetired();
        AssertFrames(await harness.ReadFramesAsync(), [1, 2], errorTerminal: true);
    }
}
