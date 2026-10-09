#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunBlockedCancellationChecksAsync()
    {
        var count = 0;
        var failures = new List<Exception>();
        foreach (var reference in new[] { false, true })
            foreach (var bytes in new[] { 16, 4096 })
                foreach (var throwingCallback in new[] { false, true })
                {
                    try
                    {
                        await CheckBlockedCancellationAsync(reference, bytes, throwingCallback);
                        Console.WriteLine($"PASS blocked-cancel/{reference}/{bytes}/{throwingCallback}: " +
                            "prepared ownership drains before transport resumes; accepted debt remains");
                        count++;
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                        Console.Error.WriteLine($"FAIL blocked-cancel/{reference}/{bytes}/{throwingCallback}: {error}");
                    }
                }
        if (failures.Count != 0) throw new AggregateException("Blocked writer cancellation checks failed.", failures);
        return count;
    }


    internal static async Task<int> RunPerStreamAbortChecksAsync()
    {
        foreach (var bytes in new[] { 16, 4096 })
        {
            await CheckPerStreamAbortAsync(bytes).WaitAsync(TimeSpan.FromSeconds(15));
            Console.WriteLine($"PASS stream-abort/{bytes}: prepared DATA canceled while writer-owned DATA remains pinned");
        }
        return 2;
    }

    private static async Task CheckPerStreamAbortAsync(int bytes)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var outgoing = new Pipe(); var incoming = new Pipe();
        var output = new PausedCancellationWriter(outgoing.Writer);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(new CancellationTransport(incoming.Reader, output),
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        Require(session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None, context.Protocol.MaxFramePayloadBytes, bytes, bytes, null)),
            "Stream-abort fixture requires negotiation.");
        using var stop = new CancellationTokenSource();
        var source = new ReadyWriterCoordinator(session, context, stop, false, 1, 3, bytes,
            bytes, bytes, 1, dynamicLifetimes: true);
        IRpcByteBufferWriter Packet(int marker)
        {
            var packet = context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, 1))
            {
                var span = packet.GetSpan(bytes + 2)[..(bytes + 2)]; span.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                BinaryPrimitives.WriteInt32LittleEndian(span[2..], marker);
                packet.Advance(bytes + 2);
            }
            return packet;
        }
        Task? pending = null;
        try
        {
            source.Attach();
            var handle = await source.AcquireStreamAsync(1, 1).WaitAsync(TimeSpan.FromSeconds(5));
            await source.EnqueueAsync(handle, Packet(10));
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = await outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            outgoing.Reader.AdvanceTo(read.Buffer.End);
            await source.EnqueueAsync(handle, Packet(20));
            pending = source.EnqueueAsync(handle, Packet(30)).AsTask();
            Require(!pending.IsCompleted && source._streams[0].Taken == 1 && source._streams[0].Released == 0,
                "Fixture must own one writer frame, one prepared frame and one blocked producer.");

            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            var abort = new OperationCanceledException("stream deadline", deadline.Token);
            Require(source.RequestStreamAbort(handle, abort), "Active generation must accept one abort request.");
            Require(!source.RequestStreamAbort(handle, abort), "Repeated abort cannot own cleanup twice.");
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Blocked producer survived stream abort."); }
            catch (OperationCanceledException error) when (error.CancellationToken == deadline.Token) { }
            Require(source._discarded == 1 && source._streams[0].Frames.Count == 0 &&
                source._streams[0].Outstanding == bytes && source._connectionCredit == 0 &&
                source._streams[0].Released == 0,
                "Abort must return only unadmitted preparation and preserve writer-owned DATA debt.");

            output.Resume.TrySetResult();
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(await source.OnWriterAsync(() => source._streams[0].Closed),
                "Writer must serialize requested close after blocked Flush resumes.");
            Require(source._streams[0].Released == 1 && !source._streams[0].Retired,
                "Writer completion alone cannot retire outstanding peer credit.");
            await source.EnqueueUpdateAsync(1, 1, bytes);
            Require(await source.OnWriterAsync(() => source._streams[0].Retired),
                "Credit return must retire aborted generation after writer settlement.");

            var next = await source.AcquireStreamAsync(2, 1).WaitAsync(TimeSpan.FromSeconds(5));
            Require(next.Generation == handle.Generation + 1 && !source.RequestStreamAbort(handle, abort),
                "Stale abort handle cannot affect reused generation.");
            await source.CloseStreamAsync(next);
        }
        finally
        {
            output.Resume.TrySetResult(); await session.DisposeAsync();
            if (pending is not null) try { await pending; } catch (Exception) { }
            try { await source.Completion; } catch (Exception) { }
            await outgoing.Reader.CompleteAsync(); await incoming.Writer.CompleteAsync();
        }
    }

    private static async Task CheckBlockedCancellationAsync(bool reference, int bytes, bool throwingCallback)
    {
        var outgoing = new Pipe();
        var incoming = new Pipe();
        var output = new PausedCancellationWriter(outgoing.Writer);
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var transport = new CancellationTransport(incoming.Reader, output);
        await using var session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        using var cancel = new CancellationTokenSource();
        var source = new ReadyWriterCoordinator(session, context, cancel, reference,
            2, 4, bytes, 8192, 16384, 1, preparedByteBudget: 8192);
        Task? pending = null;
        var callbackFailure = new InvalidOperationException("controlled external cancellation callback failure");
        using var callback = throwingCallback
            ? cancel.Token.Register(() => throw callbackFailure) : default;

        IRpcByteBufferWriter Packet(int index)
        {
            var packet = context.Buffers.Rent();
            using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(index + 1)))
            {
                var span = packet.GetSpan(bytes + 2)[..(bytes + 2)];
                span.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                packet.Advance(bytes + 2);
            }
            return packet;
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        try
        {
            source.Attach();
            await source.EnqueueAsync(0, Packet(0));
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = await outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var remaining = read.Buffer;
            try
            {
                Require(ProtocolV2FrameParser.TryReadFrame(ref remaining, context.Protocol,
                    out var header, out var payload) &&
                    header.Type == ProtocolV2FrameType.StreamData && header.RequestId == 1 &&
                    payload.Length == bytes + 2 && remaining.IsEmpty,
                    "The real sender must expose exactly the accepted DATA before Flush completes.");
            }
            finally { outgoing.Reader.AdvanceTo(read.Buffer.End); }
            Require(!output.Resume.Task.IsCompleted && source._streams[0].Released == 0,
                "The test must still own the blocked transport completion, not rely on a delay.");

            await source.EnqueueAsync(0, Packet(0));
            await source.EnqueueAsync(1, Packet(1));
            pending = source.EnqueueAsync(0, Packet(0)).AsTask();
            Require(!pending.IsCompleted, "An additional prepared buffer must be held by a capacity waiter.");

            Exception? callbackError = null;
            try { cancel.Cancel(); }
            catch (AggregateException error) { callbackError = error; }
            Require(throwingCallback
                ? callbackError is AggregateException aggregate &&
                    aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, callbackFailure))
                : callbackError is null, "External callback errors must remain visible to the cancellation caller.");
            Exception? producerError = null;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { producerError = error; }
            Require(producerError is OperationCanceledException &&
                source._streams.All(stream => stream.ProducerBusy == 0),
                "Cancellation must settle the capacity waiter without waiting for blocked I/O.");
            Require(source._discarded == 2 && source._streams.All(stream =>
                stream.Frames.Count == 0 && stream.QueuedBytes == 0),
                "Cancellation must return queued unadmitted packets while Flush is still blocked.");
            Require(!source.HasWork && source._streams.Sum(stream => stream.Taken) == 1 &&
                source._releases == 0 && !source.Completion.IsCompleted,
                "Prepared cancellation must not fabricate writer completion or keep spinning on ready data.");

            var permission = reference
                ? (long)typeof(StreamFlowController).GetField("_sendConnectionCredit",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source._reference)!
                : source._connectionCredit;
            Require(permission == 16384 - bytes,
                "Only unadmitted bytes may be refunded; peer-visible writer debt must remain.");

            cancel.Cancel();
            Require(source._discarded == 2, "Repeated cancellation must not double-return buffers.");
            output.Resume.TrySetResult();
            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Require(source._releases == 1 && source._streams.Sum(stream => stream.Taken) == 1,
                "Resuming the real writer must settle its original frame, not send canceled queued frames.");
            await session.DisposeAsync();
            try { await source.Completion; } catch (Exception) { }
            Require(source._discarded == 2, "Final pump stop must not repeat the earlier prepared cleanup.");
        }
        finally
        {
            // The red control must also release its deliberately unresponsive writer.
            output.Resume.TrySetResult();
            await session.DisposeAsync();
            if (pending is not null) try { await pending; } catch (OperationCanceledException) { }
            try { await source.Completion; } catch (Exception) { }
            await outgoing.Reader.CompleteAsync();
            await incoming.Writer.CompleteAsync();
        }
    }

    private sealed class PausedCancellationWriter(PipeWriter inner) : PipeWriter
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _first, 1) == 0)
            {
                Entered.TrySetResult();
                // Deliberately ignore cancellation until released by the fixture. Prepared
                // cleanup must not depend on transport cancellation responsiveness.
                await Resume.Task.ConfigureAwait(false);
            }
            return result;
        }
    }

    private sealed class CancellationTransport(PipeReader input, PipeWriter output) : ITransportConnection
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public PipeReader Input => input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync()
        {
            await output.CompleteAsync();
            await input.CompleteAsync();
        }
    }
}
#endif
