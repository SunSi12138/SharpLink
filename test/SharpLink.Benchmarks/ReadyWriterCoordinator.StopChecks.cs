#if SHARPLINK_READY_WRITER_EXPERIMENT
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    internal static async Task<int> RunStopChecksAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        foreach (var reference in new[] { false, true })
        {
            var (transport, peer) = await PhaseBTransportPair.CreateAsync("pipe");
            var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
            await using var session = new RpcSession(transport, new RpcSessionCreationOptions(RpcSessionRole.Client, context));
            using var cancellation = new CancellationTokenSource();
            var owner = new ReadyWriterCoordinator(session, context, cancellation, reference,
                2, 10, 16, 8192, 16384, 1, preparedByteBudget: 8192);
            var original = new InvalidOperationException("original writer stop");
            var callbackFailure = new InvalidOperationException("injected cancellation callback failure");
            ReadyStreamFrame inFlight = default;
            var hasInFlight = false;

            IRpcByteBufferWriter Packet(int index)
            {
                var packet = context.Buffers.Rent();
                using (packet.BeginPacketScope(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, (ulong)(index + 1)))
                {
                    packet.GetSpan(18)[..18].Clear();
                    packet.Advance(18);
                }
                return packet;
            }

            try
            {
                await owner.EnqueueAsync(0, Packet(0));
                hasInFlight = owner.TryTake(out inFlight);
                Check(hasInFlight, $"stop/{reference}: one admitted writer-owned frame");

                await owner.EnqueueAsync(0, Packet(0));
                await owner.EnqueueAsync(1, Packet(1));
                var pending = owner.EnqueueAsync(0, Packet(0)).AsTask();
                Check(!pending.IsCompleted, $"stop/{reference}: producer waits behind a full ring");

                using var registration = cancellation.Token.Register(() => throw callbackFailure);
                owner.Stopped(original);
                var canceled = false;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { canceled = true; }

                Check(canceled && owner._streams.All(stream => stream.ProducerBusy == 0),
                    $"stop/{reference}: throwing callback does not strand capacity producer");
                Check(owner._discarded == 2 && owner._streams.All(stream =>
                    stream.Frames.Count == 0 && stream.QueuedBytes == 0),
                    $"stop/{reference}: throwing callback does not bypass prepared-buffer cleanup");

                long available = reference
                    ? (long)(typeof(StreamFlowController).GetField("_sendConnectionCredit",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner._reference)!)
                    : owner._connectionCredit;
                Check(available == 16384 - 16,
                    $"stop/{reference}: unadmitted credits refunded without refunding writer-owned bytes");

                Exception? observed = null;
                try { await owner.Completion; }
                catch (Exception failure) { observed = failure; }
                var causes = observed is AggregateException aggregate
                    ? aggregate.Flatten().InnerExceptions.ToArray() : new[] { observed! };
                Check(causes.Any(cause => ReferenceEquals(cause, original)) &&
                    causes.Any(cause => ReferenceEquals(cause, callbackFailure)),
                    $"stop/{reference}: original and cancellation callback errors remain observable");

                owner.Stopped(new Exception("second stop must be inert"));
                Check(owner._discarded == 2 && !owner.TryTake(out _),
                    $"stop/{reference}: duplicate stop cannot double-return or admit more frames");

                context.Buffers.Return(inFlight.Packet);
                hasInFlight = false;
                owner.Released(inFlight.Slot, inFlight.CreditBytes, admitted: true, original);
                Check(owner._releases == 1 && owner._normalQueueRejected == 0,
                    $"stop/{reference}: admitted completion after stop is settled without unsent refund");
            }
            finally
            {
                owner.Stopped(original);
                if (hasInFlight) context.Buffers.Return(inFlight.Packet);
                // Cleanup the intentionally leaking old implementation after a red assertion.
                foreach (var stream in owner._streams)
                    while (stream.Frames.TryDequeue(out var packet)) context.Buffers.Return(packet);
                try { await owner.Completion; } catch (Exception) { }
                await session.DisposeAsync();
                await peer.DisposeAsync();
                context.Dispose();
            }
        }
        return checks;
    }
}
#endif
