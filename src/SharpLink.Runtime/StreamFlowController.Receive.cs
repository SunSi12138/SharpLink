namespace SharpLink.Runtime;

internal sealed partial class StreamFlowController
{
    public void AcceptReceived(long requestId, ushort streamId, int encodedBytes)
    {
        ValidateEncodedBytes(encodedBytes);
        lock (_gate)
        {
            ThrowIfTerminated();
            var key = new StreamKey(requestId, streamId);
            if (!_receiveStates.TryGetValue(key, out var state))
            {
                if (_receiveStates.Count >= _maxConcurrentStreams)
                    throw Violation("The peer exceeded the negotiated concurrent stream limit.");
                state = RentReceiveState();
                _receiveStates.Add(key, state);
            }

            if (!CanReserve(state.Credit, _receiveConnectionCredit, encodedBytes))
                throw Violation("StreamData exceeds the negotiated receive window.");
            state.Credit -= encodedBytes;
            _receiveConnectionCredit -= encodedBytes;
        }
    }

    // A frame that never enters a receiver route has no retained bytes. Check the
    // same window admission, but do not create or change a receive stream to refund it.
    internal void ValidateDiscardedReceive(long requestId, ushort streamId, int encodedBytes)
    {
        ValidateEncodedBytes(encodedBytes);
        lock (_gate)
        {
            ThrowIfTerminated();
            var credit = _receiveStates.TryGetValue(new StreamKey(requestId, streamId), out var state)
                ? state.Credit
                : _streamWindow;
            if (!CanReserve(credit, _receiveConnectionCredit, encodedBytes))
                throw Violation("Discarded StreamData exceeds the negotiated receive window.");
        }
    }

    /// <summary>Returns a non-zero credit delta when a WindowUpdate should be emitted.</summary>
    public int RecordConsumed(long requestId, ushort streamId, int encodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedBytes);
        lock (_gate)
        {
            if (_terminalException is not null)
                return 0;
            var key = new StreamKey(requestId, streamId);
            if (!_receiveStates.TryGetValue(key, out var state))
                return 0;

            var streamCredit = checked(state.Credit + encodedBytes);
            var connectionCredit = checked(_receiveConnectionCredit + encodedBytes);
            if (streamCredit > _streamWindow || connectionCredit > _connectionWindow)
            {
                throw Violation(
                    $"Consumed stream bytes exceed outstanding credit " +
                    $"(request={requestId}, stream={streamId}, bytes={encodedBytes}, " +
                    $"streamCredit={state.Credit}/{_streamWindow}, " +
                    $"connectionCredit={_receiveConnectionCredit}/{_connectionWindow}).");
            }

            state.Credit = streamCredit;
            _receiveConnectionCredit = connectionCredit;
            state.PendingConsumed = checked(state.PendingConsumed + encodedBytes);
            _pendingConnectionConsumed = checked(_pendingConnectionConsumed + encodedBytes);
            if (state.Completed)
            {
                var completedDelta = TakePendingCredit(state);
                if (state.Credit == _streamWindow)
                    RemoveReceiveState(key, state);
                return completedDelta;
            }
            if (state.PendingConsumed >= _streamUpdateThreshold)
                return TakePendingCredit(state);
            if (_pendingConnectionConsumed < _connectionUpdateThreshold)
                return 0;

            return FlushPendingConnectionCredit(key);
        }
    }

    public bool TryTakeConsumedCreditUpdate(out long requestId, out ushort streamId, out int credit)
    {
        // Connection-threshold flushes are rare. Avoid taking the flow-control gate after every
        // ordinary stream item when there are no cross-stream updates to drain.
        if (Volatile.Read(ref _consumedCreditUpdates) is null)
        {
            requestId = 0;
            streamId = 0;
            credit = 0;
            return false;
        }

        lock (_gate)
        {
            var updates = _consumedCreditUpdates;
            if (updates is null || !updates.TryDequeue(out var update))
            {
                _consumedCreditUpdates = null;
                requestId = 0;
                streamId = 0;
                credit = 0;
                return false;
            }

            requestId = update.Key.RequestId;
            streamId = update.Key.StreamId;
            credit = update.Credit;
            if (updates.Count == 0)
                _consumedCreditUpdates = null;
            return true;
        }
    }

    public int FlushConsumed(long requestId, ushort streamId)
    {
        lock (_gate)
        {
            var key = new StreamKey(requestId, streamId);
            if (_terminalException is not null ||
                !_receiveStates.TryGetValue(key, out var state))
            {
                return 0;
            }
            state.Completed = true;
            var delta = TakePendingCredit(state);
            if (state.Credit == _streamWindow)
                RemoveReceiveState(key, state);
            return delta;
        }
    }

    internal int RetainedReceiveStreamCount
    {
        get
        {
            lock (_gate)
                return _receiveStates.Count;
        }
    }
}
