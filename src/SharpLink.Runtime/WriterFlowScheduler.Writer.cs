namespace SharpLink.Runtime;

internal sealed partial class WriterFlowScheduler
{
    public bool HasWork => !_writerStopped &&
        (!_notifications.IsEmpty || (!_blocked && _ready.Count != 0));

    public bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame frame)
    {
        ArgumentNullException.ThrowIfNull(admission);
        frame = default;
        if (_writerStopped)
            return false;
        DrainNotifications();
        var connectionHeadBlocked = false;
        var node = _ready.First;
        while (node is not null)
        {
            var next = node.Next;
            var stream = node.Value;
            lock (stream.Gate)
            {
                ApplyPendingReturnLocked(stream);
                if (stream.AbortError is not null)
                {
                    ApplyAbortLocked(stream);
                    node = next;
                    continue;
                }
                if (!stream.Frames.TryPeek(out var prepared))
                {
                    RemoveReadyLocked(stream);
                    TryRetireLocked(stream);
                    node = next;
                    continue;
                }
                if (!stream.StartAllowed)
                {
                    node = next;
                    continue;
                }
                if (prepared.CreditBytes != 0)
                {
                    if (!CanReserve(stream.Credit, _streamWindow, prepared.CreditBytes))
                    {
                        node = next;
                        continue;
                    }
                    if (connectionHeadBlocked || !CanReserve(_connectionCredit, _connectionWindow, prepared.CreditBytes))
                    {
                        connectionHeadBlocked = true;
                        node = next;
                        continue;
                    }
                }
                if (!admission.TryReserve(prepared.SerializedBytes))
                    return false;
                stream.Frames.Dequeue();
                stream.QueuedBytes -= prepared.SerializedBytes;
                stream.Credit -= prepared.CreditBytes;
                _connectionCredit -= prepared.CreditBytes;
                stream.WriterPins++;
                if (prepared.Terminal)
                    stream.TerminalTaken = true;
                if (stream.Frames.Count == 0)
                    RemoveReadyLocked(stream);
                else
                    AdvanceTurnLocked(stream);
                PulseSpaceLocked(stream);
                PulseDrainLocked(stream);
                try
                {
                    _preparedBudget.Release(prepared.BudgetBytes);
                }
                catch (Exception error)
                {
                    RecordCleanupFailure(error);
                }
                frame = new WriterReadyFrame(prepared.Packet, prepared.CreditBytes, stream, prepared.ForceFlush);
                return true;
            }
        }
        _blocked = true;
        return false;
    }

    private static bool CanReserve(long credit, int window, int bytes)
        => bytes <= credit || (bytes > window && credit == window);

    private void DrainNotifications()
    {
        for (var count = 0; count < 256 && _notifications.TryDequeue(out var stream); count++)
        {
            lock (stream.Gate)
            {
                stream.NotificationQueued = false;
                if (stream.Retired)
                    continue;
                ApplyPendingReturnLocked(stream);
                if (stream.AbortError is not null)
                {
                    ApplyAbortLocked(stream);
                    continue;
                }
                if (stream.Frames.Count != 0 && !stream.Scheduled)
                {
                    stream.Scheduled = true;
                    _ready.AddLast(stream.ReadyNode);
                }
                _blocked = false;
                TryRetireLocked(stream);
            }
        }
    }

    private void ApplyPendingReturnLocked(StreamLease stream)
    {
        var pending = stream.PendingReturn;
        stream.PendingReturn = 0;
        if (pending == 0 || stream.AbortApplied)
            return;
        var returned = Math.Min(pending, _streamWindow - stream.Credit);
        stream.Credit += returned;
        _connectionCredit += returned;
        _blocked = false;
    }

    private void ApplyAbortLocked(StreamLease stream)
    {
        if (!stream.AbortApplied)
        {
            _connectionCredit += _streamWindow - stream.Credit;
            stream.Credit = _streamWindow;
            stream.PendingReturn = 0;
            stream.AbortApplied = true;
            _blocked = false;
        }
        RemoveReadyLocked(stream);
        TryRetireLocked(stream);
    }

    private void AdvanceTurnLocked(StreamLease stream)
    {
        if (!ReferenceEquals(_turn, stream))
        {
            _turn = stream;
            _turnRemaining = _quantum;
        }
        if (--_turnRemaining != 0)
            return;
        _ready.Remove(stream.ReadyNode);
        _ready.AddLast(stream.ReadyNode);
        _turn = null;
    }

    private void RemoveReadyLocked(StreamLease stream)
    {
        if (stream.Scheduled)
        {
            _ready.Remove(stream.ReadyNode);
            stream.Scheduled = false;
        }
        if (ReferenceEquals(_turn, stream))
            _turn = null;
    }

    private void Released(StreamLease stream, Exception? error)
    {
        lock (stream.Gate)
        {
            if (stream.WriterPins <= 0)
            {
                RecordCleanupFailure(new InvalidOperationException("Ready frame released twice."));
                return;
            }
            stream.WriterPins--;
            if (error is not null)
                RecordCleanupFailure(error);
            TryRetireLocked(stream);
        }
    }
}
