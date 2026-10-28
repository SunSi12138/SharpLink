namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task SynchronouslyCompletedGroupReleasesOneReservation(RpcMethodKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        var group = new ProducerGroup(3);
        var invocation = Invoke(fixture, kind, group);
        try
        {
            await group.AllDisposing.WaitAsync(Bound);
            Ensure(group.CompletedOnWriterReturn, "the real group must finish before WriteAsync returns to task tracking");
            if (kind != RpcMethodKind.OneWay)
                await fixture.CompleteReplyAsync(kind, group.RequestId);
            Ensure(await CaptureFailureAsync(invocation) is null, "the synchronous group must succeed");
            await fixture.JoinProducersAsync();
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1 && fixture.Connection.ActiveCallCount == 0,
                "synchronous completion before Track must release the group exactly once");
            group.AssertCounts();
        }
        finally
        {
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryRetryAttemptTransfersItsOwnReservationBeforeSerialization(bool finalFailure)
    {
        await using var fixture = await Fixture.CreateAsync(configure: builder => builder.UseRetry(options =>
        {
            options.MaxAttempts = 2;
            options.InitialBackoff = TimeSpan.Zero;
            options.MaxBackoff = TimeSpan.Zero;
            options.JitterRatio = 0;
        }));
        var serializations = 0;
        var codec = new ObservingCodec(() =>
        {
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "each retry attempt must transfer before serialization without consuming the unrelated selection");
            serializations++;
        });
        var invocation = Invoke(fixture, RpcMethodKind.Unary, new ProducerGroup(0), codec, idempotent: true);
        try
        {
            var first = await fixture.Transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Bound);
            await InjectUnavailableAsync(fixture, first);
            var second = await fixture.Transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(Bound);
            if (finalFailure)
                await InjectUnavailableAsync(fixture, second);
            else
                await fixture.CompleteReplyAsync(RpcMethodKind.Unary, unchecked((long)second.RequestId));
            var failure = await CaptureFailureAsync(invocation);
            Ensure(finalFailure ? failure is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } : failure is null,
                "the second attempt's selected terminal must be preserved");
            Ensure(serializations == 2 && fixture.Connection.CallAdmissionReservationCount == 1 &&
                fixture.Connection.ActiveCallCount == 0, "both attempt reservations must release exactly once");
        }
        finally
        {
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
        }
    }

    private static async Task InjectUnavailableAsync(Fixture fixture, ProtocolV2FrameHeader request)
    {
        using var payload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteError(payload, SharpLinkErrorCode.Unavailable, "retry reservation test", 1024, out _);
        await fixture.Transport.Connection.InjectFrameAsync(ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.Error, request.RequestId, payload.WrittenMemory);
    }
}
