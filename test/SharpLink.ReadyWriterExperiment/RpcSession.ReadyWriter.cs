// Experimental source: copied only into a disposable validation runtime by eng/prepare-ready-writer.py.
namespace SharpLink.Runtime;

internal interface IReadyFrameCompletion
{
    // One persistent completion target per fixed-lifecycle stream. No per-frame allocation.
    void Complete(Exception? error);
}
internal readonly record struct ReadyStreamFrame(IRpcByteBufferWriter Packet, int Slot, int CreditBytes, IReadyFrameCompletion Completion);

// Only HasWork/Signal publication cross threads; Take/Released/Stopped execute on SendPump.
// Released and Stopped MUST NOT throw. No user extension executes through this interface.
internal interface IReadyStreamWorkSource
{
    bool HasWork { get; }
    bool TryTake(out ReadyStreamFrame frame);
    void Released(int slot, int creditBytes, bool admitted, Exception? error);
    void Stopped(Exception error);
}

internal sealed partial class RpcSession
{
    internal void AttachReadyWriterExperiment(IReadyStreamWorkSource source)
        => GetOrCreatePump().AttachReadyWriterExperiment(source);
    internal void SignalReadyWriterExperiment() => GetOrCreatePump().SignalReadyWriterExperiment();

    private sealed partial class SendPump
    {
        private IReadyStreamWorkSource? _readyWriterExperiment;

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

        private bool TryReadNormalOrReadyFrame(out OwnedFrame frame)
        {
            if (_normalQueue.Reader.TryRead(out frame)) return true;
            if (Volatile.Read(ref _stopped) != 0) return false;
            var source = Volatile.Read(ref _readyWriterExperiment);
            if (source is null || !source.TryTake(out var ready)) return false;
            frame = new OwnedFrame(ready);
            if (TryReserve(frame.Length, isProtocolProgress: false)) return true;
            var rejection = SharpLinkResourceExhaustion.Create(
                SharpLinkResourceExhaustion.SendQueueCapacity, "Experimental ready frame exceeded normal queue capacity.");
            // Debit happened in this same owner turn. No writer has touched these bytes.
            try { _returnBuffer(frame.Owner); }
            finally { source.Released(ready.Slot, ready.CreditBytes, admitted: false, rejection); }
            throw rejection;
        }
    }
}
