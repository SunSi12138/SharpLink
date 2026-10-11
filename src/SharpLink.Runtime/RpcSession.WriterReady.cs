namespace SharpLink.Runtime;

// This boundary accepts already serialized frames only. A codec, compressor, user
// iterator or interceptor must never run on the connection's transport writer.
internal interface IWriterReadyCompletion
{
    // The implementation must be immutable for one stream generation and must
    // not throw. The packet has already been returned when this callback runs.
    void Complete(int creditBytes, Exception? error);
}

internal readonly record struct WriterReadyFrame(
    IRpcByteBufferWriter Packet,
    int CreditBytes,
    IWriterReadyCompletion Completion,
    bool ForceFlush = false);

internal interface IWriterReadyAdmission
{
    // At most one successful reservation per successful TryTake. The source must
    // test credit first, reserve next, and only then detach its prepared packet.
    bool TryReserve(int serializedBytes);
}

internal interface IWriterReadySource
{
    // Only Signal crosses into the pump. All the members below run exclusively
    // on its existing writer, including Stopped. No user callback may run here.
    bool HasWork { get; }
    bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame);
    void Stopped(Exception error);
}

internal sealed partial class RpcSession
{
    // Attachment transfers shutdown ownership only when this method succeeds.
    // This bridge is not enabled merely by existing in the runtime. The producer
    // and single-credit-authority adapters must explicitly attach a source.
    internal void AttachWriterReadySource(IWriterReadySource source)
        => GetOrCreatePump().AttachWriterReadySource(source);

    internal void SignalWriterReadySource() => GetOrCreatePump().SignalWriterReadySource();

    private sealed partial class SendPump : IWriterReadyAdmission
    {
        private const int OrdinaryFramesPerReadyTurn = 16;
        private const int ReadyFramesPerOrdinaryTurn = 16;

        private IWriterReadySource? _writerReadySource;
        // Written under _admissionGate by the writer, not inferred from _stopped:
        // session cancellation may finish RunAsync without calling Stop first.
        private bool _writerReadyFinished;
        private int _readyWaitingForBudget;
        private int _ordinaryFramesSinceReady;
        private int _readyFramesSinceOrdinary;
        // Writer-only transaction state; a malformed source cannot leak queue
        // capacity by reserving and then throwing or returning false.
        private bool _readyTaking;
        private bool _readyReserved;
        private int _readyReservationBytes;

        internal void AttachWriterReadySource(IWriterReadySource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            lock (_admissionGate)
            {
                if (_writerReadyFinished || Volatile.Read(ref _stopped) != 0)
                    throw CreateTransportClosedException();
                if (_writerReadySource is not null)
                    throw new InvalidOperationException("Only one stream writer may attach.");
                Volatile.Write(ref _writerReadySource, source);
            }
            _wakeup.Signal();
        }

        internal void SignalWriterReadySource() => _wakeup.Signal();

        private bool HasWriterReadyWork()
            => Volatile.Read(ref _stopped) == 0 &&
                Volatile.Read(ref _readyWaitingForBudget) == 0 &&
                (Volatile.Read(ref _writerReadySource)?.HasWork ?? false);

        // The callback is made only after attachment has been closed under the
        // same gate used by Attach. A late publisher therefore either transfers
        // ownership to this writer, or throws without transferring anything.
        private void StopWriterReadySource(Exception error)
        {
            IWriterReadySource? source;
            lock (_admissionGate)
            {
                if (_writerReadyFinished)
                    return;
                _writerReadyFinished = true;
                source = _writerReadySource;
                Volatile.Write(ref _writerReadySource, null);
            }
            source?.Stopped(error);
        }

        bool IWriterReadyAdmission.TryReserve(int serializedBytes)
        {
            if (!_readyTaking || _readyReserved)
                throw new InvalidOperationException("Invalid ready admission transaction.");
            if (serializedBytes <= 0 ||
                (serializedBytes > _normalQueueLimit && _normalQueueLimit != _maxQueuedBytes))
            {
                throw SharpLinkResourceExhaustion.Create(
                    SharpLinkResourceExhaustion.SendQueueCapacity,
                    "Ready frame cannot fit the normal send-queue allowance.");
            }

            if (!TryReserve(serializedBytes, isProtocolProgress: false))
            {
                // Arm before rechecking so a concurrent release either signals
                // this arm or is visible to the second capacity probe.
                Volatile.Write(ref _readyWaitingForBudget, 1);
                if (!TryReserve(serializedBytes, isProtocolProgress: false))
                    return false;
            }
            Volatile.Write(ref _readyWaitingForBudget, 0);
            _readyReserved = true;
            _readyReservationBytes = serializedBytes;
            return true;
        }

        private void WakeWriterReadyForCapacity()
        {
            if (Volatile.Read(ref _readyWaitingForBudget) != 0 &&
                Interlocked.Exchange(ref _readyWaitingForBudget, 0) != 0)
            {
                _wakeup.Signal();
            }
        }

        private void RollBackWriterReadyReservation()
        {
            if (!_readyReserved)
                return;
            var bytes = _readyReservationBytes;
            _readyReserved = false;
            _readyReservationBytes = 0;
            Interlocked.Add(ref _queuedBytes, -bytes);
            SharpLinkTelemetry.AddSendQueueBytes(-bytes);
            WakeWriterReadyForCapacity();
            PulseCapacityWaiters();
        }

        private bool TryTakeWriterReadyFrame(out OwnedFrame frame)
        {
            frame = default;
            if (!HasWriterReadyWork())
                return false;
            var source = Volatile.Read(ref _writerReadySource)!;
            WriterReadyFrame ready = default;
            var taken = false;
            _readyTaking = true;
            try
            {
                taken = source.TryTake(this, out ready);
                if (!taken)
                {
                    if (_readyReserved)
                        throw new InvalidOperationException("A false take retained a queue reservation.");
                    return false;
                }
                if (!_readyReserved || ready.Packet is null || ready.Completion is null ||
                    ready.CreditBytes < 0 || ready.Packet.WrittenCount != _readyReservationBytes ||
                    ready.Packet.WrittenMemory.Length != _readyReservationBytes)
                {
                    throw new InvalidOperationException("Ready packet does not match its queue reservation.");
                }
                frame = new OwnedFrame(ready);
                _readyReserved = false; // The OwnedFrame now owns this reservation.
                _readyReservationBytes = 0;
                _ordinaryFramesSinceReady = 0;
                if (_readyFramesSinceOrdinary < ReadyFramesPerOrdinaryTurn)
                    _readyFramesSinceOrdinary++;
                return true;
            }
            catch (Exception error)
            {
                // A true return transferred its buffer even when validation then
                // failed. A throwing/false source must still own its buffer.
                if (taken && ready.Packet is not null)
                {
                    try { _returnBuffer(ready.Packet); }
                    finally { ready.Completion?.Complete(ready.CreditBytes, error); }
                }
                throw;
            }
            finally
            {
                _readyTaking = false;
                RollBackWriterReadyReservation();
            }
        }

        private bool TryReadOrdinaryOrWriterReadyFrame(out OwnedFrame frame)
        {
            // No new counter updates on an unattached connection.
            if (Volatile.Read(ref _writerReadySource) is null)
                return _normalQueue.Reader.TryRead(out frame);

            // Existing progress interleaving remains outside this selection.
            // With both classes continuously ready, neither waits for the other
            // class's unbounded queue to become empty.
            if (_readyFramesSinceOrdinary != 0 &&
                _readyFramesSinceOrdinary < ReadyFramesPerOrdinaryTurn &&
                TryTakeWriterReadyFrame(out frame))
            {
                return true;
            }
            if (_ordinaryFramesSinceReady < OrdinaryFramesPerReadyTurn &&
                _normalQueue.Reader.TryRead(out frame))
            {
                _ordinaryFramesSinceReady++;
                _readyFramesSinceOrdinary = 0;
                return true;
            }
            if (TryTakeWriterReadyFrame(out frame))
                return true;
            if (_normalQueue.Reader.TryRead(out frame))
            {
                _readyFramesSinceOrdinary = 0;
                return true;
            }
            return false;
        }
    }
}
