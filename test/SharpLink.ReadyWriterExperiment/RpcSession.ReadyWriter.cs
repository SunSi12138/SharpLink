// Experimental source: copied only into a disposable validation runtime by eng/prepare-ready-writer.py.
namespace SharpLink.Runtime;

internal interface IReadyFrameCompletion
{
    // One persistent completion target per fixed-lifecycle stream. No per-frame allocation.
    void Complete(Exception? error);
}
internal readonly record struct ReadyStreamFrame(IRpcByteBufferWriter Packet, int Slot, int CreditBytes, IReadyFrameCompletion Completion);

internal interface IReadyFrameAdmission
{
    // Pump-owned budget reservation, before credit debit or removal from the stream.
    // False means transient pressure, not rejection. Impossible frames may throw.
    bool TryReserve(int frameBytes);
}

// Only HasWork/Signal publication cross threads; Take/Released/Stopped execute on SendPump.
// Released and Stopped MUST NOT throw. No user extension executes through this interface.
internal interface IReadyStreamWorkSource
{
    bool HasWork { get; }
    bool TryTake(IReadyFrameAdmission admission, out ReadyStreamFrame frame);
    void Released(int slot, int creditBytes, bool admitted, Exception? error);
    void Stopped(Exception error);
}

internal sealed partial class RpcSession
{
    internal void AttachReadyWriterExperiment(IReadyStreamWorkSource source)
        => GetOrCreatePump().AttachReadyWriterExperiment(source);
    internal void SignalReadyWriterExperiment() => GetOrCreatePump().SignalReadyWriterExperiment();

    private sealed partial class SendPump : IReadyFrameAdmission
    {
        private IReadyStreamWorkSource? _readyWriterExperiment;
        private int _readyWaitingForBudget;

        internal void AttachReadyWriterExperiment(IReadyStreamWorkSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (Interlocked.CompareExchange(ref _readyWriterExperiment, source, null) is not null)
                throw new InvalidOperationException("Only one connection owner may attach.");
            if (Volatile.Read(ref _stopped) != 0)
            {
                source.Stopped(CreateTransportClosedException());
                throw CreateTransportClosedException();
            }
            _wakeup.Signal();
        }

        internal void SignalReadyWriterExperiment() => _wakeup.Signal();

        private bool HasReadyWriterWork()
            => Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _readyWaitingForBudget) == 0 &&
                (Volatile.Read(ref _readyWriterExperiment)?.HasWork ?? false);

        bool IReadyFrameAdmission.TryReserve(int frameBytes)
        {
            if (frameBytes < 0 || frameBytes > _normalQueueLimit && _normalQueueLimit != _maxQueuedBytes)
                throw SharpLinkResourceExhaustion.Create(SharpLinkResourceExhaustion.SendQueueCapacity,
                    "Experimental ready frame can never fit the normal send-queue allowance.");
            if (TryReserve(frameBytes, isProtocolProgress: false))
            {
                Volatile.Write(ref _readyWaitingForBudget, 0);
                return true;
            }
            // Arm before rechecking: a concurrent reservation release must either
            // wake this arm or be visible to the second reservation attempt.
            Volatile.Write(ref _readyWaitingForBudget, 1);
            if (!TryReserve(frameBytes, isProtocolProgress: false)) return false;
            Volatile.Write(ref _readyWaitingForBudget, 0);
            return true;
        }

        private void WakeReadyWriterForCapacity()
        {
            // No extra RMW on ordinary frame release. Only an armed budget wait
            // needs a signal; the existing byte-budget accounting remains authoritative.
            if (Volatile.Read(ref _readyWaitingForBudget) != 0 &&
                Interlocked.Exchange(ref _readyWaitingForBudget, 0) != 0)
                _wakeup.Signal();
        }

        private bool TryReadNormalOrReadyFrame(out OwnedFrame frame)
        {
            if (_normalQueue.Reader.TryRead(out frame)) return true;
            if (Volatile.Read(ref _stopped) != 0) return false;
            var source = Volatile.Read(ref _readyWriterExperiment);
            if (source is null || !source.TryTake(this, out var ready)) return false;
            // The source reserved this exact serialized length before relinquishing
            // its frame. A false take leaves it queued and allows the pending batch
            // to flush; there is no dequeue/refund/re-enqueue cycle.
            frame = new OwnedFrame(ready);
            return true;
        }
    }
}
