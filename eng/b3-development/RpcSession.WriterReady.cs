namespace SharpLink.Runtime;

// Only pre-serialized, framework-owned frames cross this boundary.
internal interface IWriterReadyCompletion
{
    void Complete(int creditBytes, Exception? error);
}
internal readonly record struct WriterReadyFrame(IRpcByteBufferWriter Packet, int CreditBytes,
    IWriterReadyCompletion Completion, bool ForceFlush = false);
internal interface IWriterReadyAdmission
{
    bool TryReserve(int serializedBytes);
}
internal interface IWriterReadySource
{
    bool HasWork { get; }
    bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame);
    void Stopped(Exception error);
}

internal sealed partial class RpcSession
{
    internal void AttachWriterReadySource(IWriterReadySource source) => GetOrCreatePump().AttachWriterReadySource(source);
    internal void SignalWriterReadySource() => GetOrCreatePump().SignalWriterReadySource();

    private sealed partial class SendPump : IWriterReadyAdmission
    {
        private const int OrdinaryFramesPerReadyTurn = 16;
        private const int ReadyFramesPerOrdinaryTurn = 16;
        private IWriterReadySource? _writerReadySource;
        private bool _writerReadyFinished;
        private int _readyWaitingForBudget, _ordinaryFramesSinceReady, _readyFramesSinceOrdinary;
        private bool _readyTaking, _readyReserved;
        private int _readyReservationBytes;

        internal void AttachWriterReadySource(IWriterReadySource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            lock (_admissionGate)
            {
                if (_writerReadyFinished || Volatile.Read(ref _stopped) != 0) throw CreateTransportClosedException();
                if (_writerReadySource is not null) throw new InvalidOperationException("Only one stream writer may attach.");
                Volatile.Write(ref _writerReadySource, source);
            }
            _wakeup.Signal();
        }
        internal void SignalWriterReadySource() => _wakeup.Signal();
        private bool HasWriterReadyWork() => Volatile.Read(ref _stopped) == 0 &&
            Volatile.Read(ref _readyWaitingForBudget) == 0 &&
            (Volatile.Read(ref _writerReadySource)?.HasWork ?? false);
        private void StopWriterReadySource(Exception error)
        {
            IWriterReadySource? source;
            lock (_admissionGate)
            {
                if (_writerReadyFinished) return;
                _writerReadyFinished = true;
                source = _writerReadySource;
                Volatile.Write(ref _writerReadySource, null);
            }
            source?.Stopped(error);
        }
        bool IWriterReadyAdmission.TryReserve(int bytes)
        {
            if (!_readyTaking || _readyReserved) throw new InvalidOperationException("Invalid ready admission transaction.");
            if (bytes <= 0 || (bytes > _normalQueueLimit && _normalQueueLimit != _maxQueuedBytes))
                throw SharpLinkResourceExhaustion.Create(SharpLinkResourceExhaustion.SendQueueCapacity,
                    "Ready frame cannot fit the normal send-queue allowance.");
            if (!TryReserve(bytes, false))
            {
                Volatile.Write(ref _readyWaitingForBudget, 1);
                if (!TryReserve(bytes, false)) return false;
            }
            Volatile.Write(ref _readyWaitingForBudget, 0);
            _readyReserved = true;
            _readyReservationBytes = bytes;
            return true;
        }
        private void WakeWriterReadyForCapacity()
        {
            if (Volatile.Read(ref _readyWaitingForBudget) != 0 && Interlocked.Exchange(ref _readyWaitingForBudget, 0) != 0)
                _wakeup.Signal();
        }
        private void RollBackWriterReadyReservation()
        {
            if (!_readyReserved) return;
            var bytes = _readyReservationBytes;
            _readyReserved = false; _readyReservationBytes = 0;
            Interlocked.Add(ref _queuedBytes, -bytes);
            SharpLinkTelemetry.AddSendQueueBytes(-bytes);
            WakeWriterReadyForCapacity(); PulseCapacityWaiters();
        }
        private bool TryTakeWriterReadyFrame(out OwnedFrame frame)
        {
            frame = default;
            if (!HasWriterReadyWork()) return false;
            var source = Volatile.Read(ref _writerReadySource)!;
            WriterReadyFrame ready = default;
            bool taken = false;
            _readyTaking = true;
            try
            {
                taken = source.TryTake(this, out ready);
                if (!taken)
                {
                    if (_readyReserved) throw new InvalidOperationException("A false take retained a reservation.");
                    return false;
                }
                if (!_readyReserved || ready.Packet is null || ready.Completion is null || ready.CreditBytes < 0 ||
                    ready.Packet.WrittenCount != _readyReservationBytes || ready.Packet.WrittenMemory.Length != _readyReservationBytes)
                    throw new InvalidOperationException("Ready packet does not match its reservation.");
                frame = new OwnedFrame(ready);
                _readyReserved = false; _readyReservationBytes = 0;
                _ordinaryFramesSinceReady = 0;
                if (_readyFramesSinceOrdinary < ReadyFramesPerOrdinaryTurn) _readyFramesSinceOrdinary++;
                return true;
            }
            catch (Exception error)
            {
                if (taken && ready.Packet is not null)
                {
                    try { _returnBuffer(ready.Packet); }
                    finally { ready.Completion?.Complete(ready.CreditBytes, error); }
                }
                throw;
            }
            finally { _readyTaking = false; RollBackWriterReadyReservation(); }
        }
        private bool TryReadOrdinaryOrWriterReadyFrame(out OwnedFrame frame)
        {
            if (Volatile.Read(ref _writerReadySource) is null) return _normalQueue.Reader.TryRead(out frame);
            if (_readyFramesSinceOrdinary != 0 && _readyFramesSinceOrdinary < ReadyFramesPerOrdinaryTurn &&
                TryTakeWriterReadyFrame(out frame)) return true;
            if (_ordinaryFramesSinceReady < OrdinaryFramesPerReadyTurn && _normalQueue.Reader.TryRead(out frame))
            { _ordinaryFramesSinceReady++; _readyFramesSinceOrdinary = 0; return true; }
            if (TryTakeWriterReadyFrame(out frame)) return true;
            if (_normalQueue.Reader.TryRead(out frame)) { _readyFramesSinceOrdinary = 0; return true; }
            return false;
        }
    }
}
