namespace SharpLink.Runtime;

internal sealed partial class WriterOwnedStreamScheduler
{
    // These members and pump completion callbacks execute on one SendPump, never concurrently.
    public bool HasWork => Volatile.Read(ref _stopped) == 0 &&
        (!_changes.IsEmpty || (!_blocked && _ready.Count != 0));

    private void DrainChanges()
    {
        for (var index = 0; index < 256 && _changes.TryDequeue(out var stream); index++)
        {
            Interlocked.Exchange(ref stream.ChangeQueued, 0);
            lock (stream.Gate)
            {
                if (!stream.Attached) continue;
                var returned = stream.PendingCredit;
                stream.PendingCredit = 0;
                if (stream.WireAttached && returned != 0)
                {
                    // Both ledgers use the actual return for THIS stream. Independent clamps
                    // would turn an excess/late-after-abort update into another stream's credit.
                    var delta = Math.Min(returned, _streamWindow - stream.Credit);
                    stream.Credit += delta;
                    _connectionCredit = checked(_connectionCredit + delta);
                }
                if (stream.Error is not null)
                {
                    stream.OwnerClosed = true;
                    if (stream.RemoteAbort && !stream.AbortApplied)
                    {
                        _connectionCredit = checked(_connectionCredit + _streamWindow - stream.Credit);
                        stream.Credit = _streamWindow;
                        stream.AbortApplied = true;
                    }
                }
                if (stream.Frames.Count != 0 && stream.Error is null)
                {
                    if (stream.Node.List is null) _ready.AddLast(stream.Node);
                }
                else
                {
                    if (stream.Node.List is not null) _ready.Remove(stream.Node);
                    stream.Scheduled = false;
                }
                _blocked = false;
            }
            TryRetire(stream);
        }
        if (Volatile.Read(ref _cleanupFailure) is { } failure)
            throw new InvalidOperationException("Prepared stream buffer cleanup failed.", failure);
    }

    public bool TryTake(IWriterReadyAdmission admission, out WriterReadyFrame result)
    {
        ArgumentNullException.ThrowIfNull(admission);
        result = default;
        if (Volatile.Read(ref _stopped) != 0) return false;
        DrainChanges();
        var node = _ready.First;
        var connectionBlocked = false;
        while (node is not null)
        {
            var stream = node.Value;
            var next = node.Next;
            PreparedFrame frame;
            TaskCompletionSource? space;
            lock (stream.Gate)
            {
                if (stream.Error is not null || !stream.Frames.TryPeek(out frame))
                {
                    _ready.Remove(node);
                    stream.Scheduled = false;
                    node = next;
                    continue;
                }
                if (!frame.Terminal)
                {
                    if (connectionBlocked) { node = next; continue; }
                    if (!CanReserve(_connectionCredit, _connectionWindow, frame.CreditBytes))
                    {
                        // Do not admit later DATA ahead of this connection-credit waiter.
                        // Still service credit-free EOFs of other streams: those can cause the
                        // peer to flush sub-threshold credit that unblocks this very waiter.
                        connectionBlocked = true;
                        node = next;
                        continue;
                    }
                    if (!CanReserve(stream.Credit, _streamWindow, frame.CreditBytes))
                    { node = next; continue; }
                }
                if (!admission.TryReserve(frame.Bytes)) return false;
                stream.Frames.Dequeue();
                if (frame.Terminal) stream.OwnerClosed = true;
                else
                {
                    stream.WireAttached = true;
                    stream.Credit -= frame.CreditBytes;
                    _connectionCredit -= frame.CreditBytes;
                }
                stream.WriterReferences++;
                _ready.Remove(node);
                // Item-level round robin preserves the connection-waiter order; no speculative
                // grant or per-stream burst may bypass an already waiting connection head.
                if (stream.Frames.Count != 0) _ready.AddLast(node);
                else stream.Scheduled = false;
                space = stream.SpaceChanged;
                stream.SpaceChanged = null;
            }
            // The existing pump budget was reserved BEFORE debit/detachment. Its ownership
            // replaces the prepared-byte reservation. Do not read a returned packet's length.
            _prepared.Release(frame.Bytes);
            space?.TrySetResult();
            result = new WriterReadyFrame(frame.Packet, frame.CreditBytes, stream, frame.ForceFlush);
            return true;
        }
        _blocked = _ready.Count != 0;
        return false;
    }

    private void Released(StreamHandle stream, Exception? error)
    {
        List<DrainWaiter>? completed = null;
        lock (stream.Gate)
        {
            if (stream.WriterReferences <= 0)
                throw new InvalidOperationException("Writer frame was settled more than once.");
            stream.WriterReferences--;
            stream.ReleasedFrames++;
            if (error is not null) stream.Error ??= error;
            if (stream.DrainWaiters is { } waiters)
            {
                for (var index = waiters.Count - 1; index >= 0; index--)
                {
                    if (error is null && waiters[index].Target > stream.ReleasedFrames) continue;
                    (completed ??= []).Add(waiters[index]);
                    waiters.RemoveAt(index);
                }
                if (waiters.Count == 0) stream.DrainWaiters = null;
            }
        }
        CompleteDrainWaiters(completed, error);
        TryRetire(stream);
    }

    private void TryRetire(StreamHandle stream)
    {
        lock (stream.Gate)
        {
            if (!stream.Attached || !stream.OwnerClosed || stream.Credit != _streamWindow ||
                stream.WriterReferences != 0 || stream.ProducerActive != 0 || stream.Frames.Count != 0)
                return;
            stream.Attached = false;
            if (stream.Node.List is not null) _ready.Remove(stream.Node);
        }
        TaskCompletionSource? capacity = null;
        lock (_mapGate)
        {
            var key = new StreamKey(stream.RequestId, stream.StreamId);
            if (_streams.TryGetValue(key, out var current) && ReferenceEquals(current, stream))
            {
                _streams.Remove(key);
                capacity = _capacityChanged;
                _capacityChanged = null;
            }
        }
        capacity?.TrySetResult();
    }

    // Diagnostics only, at an owner-quiescent boundary. Never added to the item hot path.
    internal void AssertCreditConservation()
    {
        long debt = 0;
        lock (_mapGate)
            foreach (var stream in _streams.Values)
            {
                if (stream.Credit > _streamWindow) throw new InvalidOperationException("Stream credit exceeded its window.");
                debt = checked(debt + _streamWindow - stream.Credit);
            }
        if (checked(_connectionCredit + debt) != _connectionWindow)
            throw new InvalidOperationException("Connection credit conservation failed.");
    }
}
