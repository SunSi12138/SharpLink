using System.Diagnostics.Metrics;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcSessionRefreshAccountingTests
{
    [Test]
    [NotInParallel]
    public async Task InternallyConsumedRefreshFramesShouldRecordBytesAndActivityExactlyOnce()
    {
        var clock = new ManualTimeProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "refresh-accounting", input.Reader, output.Writer,
            RpcSessionTestFixture.ClientOptions(context), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.SessionRefresh);

        long receivedBytes = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.transport.bytes.received")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            Interlocked.Add(ref receivedBytes, measurement));
        listener.Start();

        var request = new ProtocolV2SessionRefreshRequested(Guid.NewGuid(), 2);
        var writer = session.RentFrameWriter();
        byte[] frames;
        try
        {
            for (var index = 0; index < 2; index++)
            {
                using (writer.BeginPacketScope(
                           ProtocolV2FrameType.SessionRefreshRequested, ProtocolV2FrameFlags.None, 0))
                    ProtocolV2PayloadCodec.WriteSessionRefreshRequested(writer, request);
            }
            frames = writer.WrittenSpan.ToArray();
        }
        finally
        {
            context.Buffers.Return(writer);
        }

        clock.Advance(TimeSpan.FromSeconds(5));
        Ensure(session.TimeSinceLastActivity == TimeSpan.FromSeconds(5), "session starts inactive");
        var notifications = 0;
        session.SessionRefreshRequested += actual =>
        {
            notifications++;
            Ensure(actual == request, "dispatch preserves the desired generation");
            Ensure(receivedBytes == notifications * (ProtocolV2Constants.HeaderBytes + 24),
                "each complete refresh frame is counted before dispatch");
            Ensure(session.TimeSinceLastActivity == TimeSpan.Zero &&
                   session.LastActive == clock.GetUtcNow().UtcDateTime,
                "monotonic and UTC activity timestamps are updated before dispatch");
        };

        // An incomplete frame must neither count traffic nor dispatch a notification.
        var partial = new ReadOnlySequence<byte>(frames.AsMemory(0, ProtocolV2Constants.HeaderBytes + 23));
        Ensure(!session.TryReadInboundFrame(ref partial, context.Protocol, out _, out _) &&
               receivedBytes == 0 && notifications == 0 &&
               session.TimeSinceLastActivity == TimeSpan.FromSeconds(5),
            "an incomplete frame leaves accounting and activity unchanged");

        var buffer = new ReadOnlySequence<byte>(frames);
        Ensure(!session.TryReadInboundFrame(ref buffer, context.Protocol, out _, out _),
            "refresh-only input is internally consumed without returning an RPC frame");
        Ensure(buffer.IsEmpty && notifications == 2 && receivedBytes == frames.Length,
            "consecutive refresh frames are each consumed and counted exactly once");
        Ensure(!session.TryReadInboundFrame(ref buffer, context.Protocol, out _, out _) &&
               receivedBytes == frames.Length && notifications == 2,
            "re-reading the empty buffer does not duplicate accounting");

        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
