namespace SharpLink.Runtime;

internal sealed partial class StreamFlowController
{
    private SendState RentSendState()
    {
        var state = _pooledSendStates;
        if (state is null)
        {
            state = new SendState(_streamWindow);
        }
        else
        {
            var next = state.Next;
            _pooledSendStates = next;
            if (next is not null)
            {
                Debug.Assert(_pooledSendStateOverflowCount > 0);
                _pooledSendStateOverflowCount--;
            }
            else
            {
                Debug.Assert(_pooledSendStateOverflowCount == 0);
            }

            state.Next = null;
            state.Credit = _streamWindow;
            state.Completed = false;
            state.AbortException = null;
            state.CreditRefundedByAbort = false;
        }

        state.Lease = ++_sendStateLeaseCounter;
        return state;
    }

    private void RemoveSendState(StreamKey key, SendState state)
    {
        if (_sendStates.Remove(key))
            ReturnSendState(state);
    }

    private void ReturnSendState(SendState state)
    {
        Debug.Assert(state.Completed);
        Debug.Assert(state.Credit == _streamWindow);
        Debug.Assert(state.Next is null);
        // No routed state/producer can observe this pooled generation. Waiter completions
        // own their original exception independently; the pool must not retain its stack/ALC.
        state.AbortException = null;
        var pooledState = _pooledSendStates;
        if (pooledState is null)
        {
            Debug.Assert(_pooledSendStateOverflowCount == 0);
            _pooledSendStates = state;
            return;
        }

        if (_pooledSendStateOverflowCount >= _maxPooledSendStates - 1)
            return;

        state.Next = pooledState;
        _pooledSendStates = state;
        _pooledSendStateOverflowCount++;
    }

    private void ClearPooledSendStates()
    {
        var state = _pooledSendStates;
        _pooledSendStates = null;
        _pooledSendStateOverflowCount = 0;
        while (state is not null)
        {
            var next = state.Next;
            state.Next = null;
            state = next;
        }
    }

    private ReceiveState RentReceiveState()
    {
        var state = _pooledReceiveStates;
        if (state is null)
            return new ReceiveState(_streamWindow);

        Debug.Assert(state.Credit == _streamWindow);
        Debug.Assert(state.PendingConsumed == 0);
        Debug.Assert(state.Completed);
        var next = state.Next;
        _pooledReceiveStates = next;
        if (next is not null)
        {
            Debug.Assert(_pooledReceiveStateOverflowCount > 0);
            _pooledReceiveStateOverflowCount--;
            state.Next = null;
        }
        else
        {
            Debug.Assert(_pooledReceiveStateOverflowCount == 0);
        }

        state.Completed = false;
        return state;
    }

    private void RemoveReceiveState(StreamKey key, ReceiveState state)
    {
        if (_receiveStates.Remove(key))
            ReturnReceiveState(state);
    }

    private void ReturnReceiveState(ReceiveState state)
    {
        Debug.Assert(state.Credit == _streamWindow);
        Debug.Assert(state.PendingConsumed == 0);
        Debug.Assert(state.Completed);
        Debug.Assert(state.Next is null);
        var pooledState = _pooledReceiveStates;
        if (pooledState is null)
        {
            Debug.Assert(_pooledReceiveStateOverflowCount == 0);
            _pooledReceiveStates = state;
            return;
        }

        if (_pooledReceiveStateOverflowCount >= _maxPooledReceiveStates - 1)
            return;

        state.Next = pooledState;
        _pooledReceiveStates = state;
        _pooledReceiveStateOverflowCount++;
    }

    private void ClearPooledReceiveStates()
    {
        var state = _pooledReceiveStates;
        _pooledReceiveStates = null;
        _pooledReceiveStateOverflowCount = 0;
        while (state is not null)
        {
            var next = state.Next;
            state.Clear();
            state = next;
        }
    }

}
