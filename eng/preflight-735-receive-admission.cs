namespace SharpLink.Runtime;

// Isolated first-admission prototype. Existing keyed/resolved APIs and production
// route registration remain unchanged; adoption requires a separate lifecycle review.
internal sealed partial class StreamFlowController
{
    internal ResolvedReceiveCreditLease AcceptReceivedCreditLease(
        long requestId, ushort streamId, int encodedBytes)
    {
        ValidateEncodedBytes(encodedBytes);
        var key = new StreamKey(requestId, streamId);
        lock (_gate)
        {
            ThrowIfTerminated();
            if (!_receiveStates.TryGetValue(key, out var state))
            {
                if (_receiveStates.Count >= _maxConcurrentStreams)
                    throw Violation("The peer exceeded the negotiated concurrent stream limit.");
                // A rejected first DATA frame must not publish an ownerless receive state.
                if (!CanReserve(_streamWindow, _receiveConnectionCredit, encodedBytes))
                    throw Violation("StreamData exceeds the negotiated receive window.");
                state = RentReceiveState();
                state.Attached = true;
                try
                {
                    _receiveStates.Add(key, state);
                }
                catch
                {
                    state.Attached = false;
                    state.Completed = true;
                    ReturnReceiveState(state);
                    throw;
                }
            }
            else
            {
                if (state.Completed)
                    throw CreateStreamClosedException();
                if (!CanReserve(state.Credit, _receiveConnectionCredit, encodedBytes))
                    throw Violation("StreamData exceeds the negotiated receive window.");
            }

            state.Credit -= encodedBytes;
            _receiveConnectionCredit -= encodedBytes;
            // The debit and immutable generation snapshot share one linearization point.
            return new ResolvedReceiveCreditLease(this, requestId, streamId, state, state.Lease);
        }
    }
}
