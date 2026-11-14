using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Text.Json;

namespace SharpLink.UnitTests.Runtime;

public sealed class DynamicInputDrainOrderingTests
{
    [Test]
    public async Task AllInputGatesMustCloseBeforeSynchronousCreditCleanup()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("dynamic-input-close-order",
            input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions(context), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 16, connectionReceiveWindowBytes: 64);
        var manager = session.StreamManager;
        manager.ReservePreAdmissionStreams(1, 2, context.Buffers, _ => true, _ => { },
            () => throw new InvalidOperationException("unexpected capacity"), retainUntilLocalCompletion: true);
        var firstCodec = new CountingCodec();
        var secondCodec = new CountingCodec();
        _ = session.CreateGeneratedInboundStream(1, 1, firstCodec, false, default);
        _ = session.CreateGeneratedInboundStream(1, 2, secondCodec, false, default);
        await manager.DispatchChunkAsync(1, 1, new ReadOnlySequence<byte>(new byte[8]));
        var captured = manager.CaptureInputStreamDrain(1, 2);
        var creditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var creditRelease = new ManualResetEventSlim();
        var cleanupScope = new AsyncLocal<bool>();
        var claimed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            // Only this drain inherits the scope; other tests' Sessions cannot claim the gate.
            if (!cleanupScope.Value || bytes <= 0 || Interlocked.Exchange(ref claimed, 1) != 0)
                return;
            creditEntered.TrySetResult();
            if (!creditRelease.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("synchronous receive-credit cleanup watchdog");
        });
        listener.Start();
        Task? drain = null;
        try
        {
            drain = Task.Run(async () =>
            {
                cleanupScope.Value = true;
                await captured.CompleteAsync();
            });
            await creditEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first real pooled child's DisposeAsync is still in its synchronous prefix,
            // returning buffered receive credit through the real Session and public metric.
            await manager.DispatchChunkAsync(1, 2, new ReadOnlySequence<byte>(new byte[4]));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                firstCodecCalls = firstCodec.Calls,
                secondCodecCalls = secondCodec.Calls,
                lateSecondStreamEnteredBusinessCodec = secondCodec.Calls != 0,
                drainCompleted = drain.IsCompleted,
                scalarRoutes = manager.ActiveStreamCount
            }));
            Ensure(firstCodec.Calls == 1 && secondCodec.Calls == 0 && !drain.IsCompleted &&
                manager.ActiveStreamCount == 2,
                $"DYNAMIC_INPUT_CLOSE_ALL_BEFORE_CLEANUP: first={firstCodec.Calls}, " +
                $"second={secondCodec.Calls}, drainCompleted={drain.IsCompleted}, routes={manager.ActiveStreamCount}");
            creditRelease.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(manager.ActiveStreamCount == 2, "local child exit must preserve scalar routes before peer terminal");
        }
        finally
        {
            creditRelease.Set();
            if (drain is not null)
                await drain.WaitAsync(TimeSpan.FromSeconds(10));
            manager.CompletePeerStream(1, 1, null);
            manager.CompletePeerStream(1, 2, null);
            await session.DisposeAsync();
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
        Ensure(manager.ActiveStreamCount == 0, "peer terminal must reclaim both routes");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingCodec : IRpcCodec<int>
    {
        public int Calls;
        public void Serialize(in int value, IBufferWriter<byte> writer) => throw new NotSupportedException();
        public int Deserialize(in ReadOnlySequence<byte> bytes)
        {
            Interlocked.Increment(ref Calls);
            return 1;
        }
    }
}
