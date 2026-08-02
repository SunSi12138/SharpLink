namespace SharpLink.Runtime;

/// <summary>
/// Coordinates negotiated byte credits for every stream and for the owning connection.
/// The uncontended path is allocation-free; waiters are allocated only after credit is exhausted.
/// </summary>
internal sealed class StreamFlowController
{
    private readonly Lock _gate = new();
    private readonly int _streamWindow;
    private readonly int _connectionWindow;
    private readonly int _maxFramePayloadBytes;
    private readonly int _maxConcurrentStreams;
    private readonly int _streamUpdateThreshold;
    private readonly int _connectionUpdateThreshold;
    private readonly Dictionary<StreamKey, SendState> _sendStates = [];
    private readonly Dictionary<StreamKey, ReceiveState> _receiveStates = [];
    private readonly LinkedList<CreditWaiter> _waiters = [];
    private Queue<ConsumedCreditUpdate>? _consumedCreditUpdates;
    // Completed states can remain as tombstones until their final in-flight credit arrives.
    // The active count distinguishes hard live-stream exhaustion from tombstone pressure;
    // the state dictionary itself remains bounded by the negotiated stream limit.
    private int _activeSendStreamCount;
    private long _sendConnectionCredit;
    private long _receiveConnectionCredit;
    private long _pendingConnectionConsumed;
    private Exception? _terminalException;

    public StreamFlowController(
        int streamWindow,
        int connectionWindow,
        int maxFramePayloadBytes,
        int maxConcurrentStreams = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectionWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFramePayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentStreams);
        if (connectionWindow < streamWindow)
            throw new ArgumentException("The connection window cannot be smaller than the stream window.");

        _streamWindow = streamWindow;
        _connectionWindow = connectionWindow;
        _maxFramePayloadBytes = maxFramePayloadBytes;
        _maxConcurrentStreams = maxConcurrentStreams;
        _streamUpdateThreshold = Math.Max(1, streamWindow / 2);
        _connectionUpdateThreshold = Math.Max(1, connectionWindow / 2);
        _sendConnectionCredit = connectionWindow;
        _receiveConnectionCredit = connectionWindow;
    }

    public ValueTask AcquireSendCreditAsync(
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        ValidateEncodedBytes(encodedBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var key = new StreamKey(requestId, streamId);
        SendState? state;
        lock (_gate)
        {
            ThrowIfTerminated();
            if (!_sendStates.TryGetValue(key, out state))
            {
                if (_sendStates.Count < _maxConcurrentStreams)
                {
                    state = AddSendState(key);
                }
                else if (_activeSendStreamCount >= _maxConcurrentStreams)
                {
                    throw CreateConcurrentStreamLimitException();
                }
            }
            if (state is not null)
            {
                if (state.AbortException is { } abortException)
                    throw abortException;
                if (state.Completed)
                    throw CreateStreamClosedException();
                if (_waiters.Count == 0 && CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
                {
                    Reserve(state, encodedBytes);
                    return ValueTask.CompletedTask;
                }
            }
        }

        return AcquireContendedSendCreditAsync(key, state, encodedBytes, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask AcquireContendedSendCreditAsync(
        StreamKey key,
        SendState? expectedState,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreditWaiter waiter;
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            ThrowIfTerminated();
            SendState? state = null;
            if (_sendStates.TryGetValue(key, out var existingState))
            {
                if (expectedState is not null && !ReferenceEquals(existingState, expectedState))
                    throw CreateStreamClosedException();
                state = existingState;
            }
            else if (expectedState is not null)
            {
                throw CreateStreamClosedException();
            }
            else if (_sendStates.Count < _maxConcurrentStreams)
            {
                state = AddSendState(key);
            }

            if (state is not null)
            {
                if (state.AbortException is { } abortException)
                    throw abortException;
                if (state.Completed)
                    throw CreateStreamClosedException();
                if (_waiters.Count == 0 && CanReserve(state.Credit, _sendConnectionCredit, encodedBytes))
                {
                    Reserve(state, encodedBytes);
                    return ValueTask.CompletedTask;
                }
            }

            waiter = new CreditWaiter(this, key, encodedBytes, canCreateState: expectedState is null);
            waiter.Node = _waiters.AddLast(waiter);
            ready = AdmitWaiters();
        }

        CompleteReadyWaiters(ready);
        return new ValueTask(waiter.WaitAsync(cancellationToken));
    }

    public void ApplyWindowUpdate(long requestId, ushort streamId, int credit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credit);
        List<CreditWaiter>? ready = null;
        lock (_gate)
        {
            ThrowIfTerminated();
            var key = new StreamKey(requestId, streamId);
            if (!_sendStates.TryGetValue(key, out var state))
                throw Violation("WindowUpdate references an unknown stream.");

            var updatedStreamCredit = checked(state.Credit + credit);
            var updatedConnectionCredit = checked(_sendConnectionCredit + credit);
            if (updatedStreamCredit > _streamWindow)
                throw Violation("WindowUpdate exceeds the negotiated stream receive window.");
            if (updatedConnectionCredit > _connectionWindow)
                throw Violation("WindowUpdate exceeds the negotiated connection receive window.");

            state.Credit = updatedStreamCredit;
            _sendConnectionCredit = updatedConnectionCredit;
            if (state.Completed && state.Credit == _streamWindow)
                _sendStates.Remove(key);
            ready = AdmitWaiters();
        }

        CompleteReadyWaiters(ready);
    }

    public void ReturnUnsentCredit(long requestId, ushort streamId, int credit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credit);
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            if (_terminalException is not null)
                return;
            var key = new StreamKey(requestId, streamId);
            if (!_sendStates.TryGetValue(key, out var state) || state.AbortException is not null)
                return;

            var updatedStreamCredit = checked(state.Credit + credit);
            var updatedConnectionCredit = checked(_sendConnectionCredit + credit);
            if (updatedStreamCredit > _streamWindow || updatedConnectionCredit > _connectionWindow)
            {
                throw new InvalidOperationException(
                    "Unsent stream credit was returned more than once.");
            }

            state.Credit = updatedStreamCredit;
            _sendConnectionCredit = updatedConnectionCredit;
            if (state.Completed && state.Credit == _streamWindow)
                _sendStates.Remove(key);
            ready = AdmitWaiters();
        }

        CompleteReadyWaiters(ready);
    }

    public void CompleteSendStream(long requestId, ushort streamId, Exception? exception = null)
    {
        List<CreditWaiter>? rejected = null;
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            var key = new StreamKey(requestId, streamId);
            if (_sendStates.TryGetValue(key, out var state))
            {
                if (!state.Completed)
                    _activeSendStreamCount--;
                if (state.AbortException is not null)
                {
                    state.Completed = true;
                }
                else
                {
                    state.Completed = true;
                    state.AbortException = exception;
                }
                // The receiver may already have consumed bytes and have a WindowUpdate in
                // flight. Keep the terminal state until all outstanding credit is returned;
                // deleting it here would turn that valid late update into ProtocolViolation.
                if (state.Credit == _streamWindow)
                    _sendStates.Remove(key);
            }

            var node = _waiters.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key == key)
                {
                    _waiters.Remove(node);
                    node.Value.Node = null;
                    (rejected ??= []).Add(node.Value);
                }
                node = next;
            }
            ready = AdmitWaiters();
        }

        var completionException = exception ?? CreateStreamClosedException();
        if (rejected is not null)
        {
            for (var index = 0; index < rejected.Count; index++)
                rejected[index].Completion.TrySetException(completionException);
        }
        CompleteReadyWaiters(ready);
    }

    public void AbortSendStreams(long requestId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<StreamKey>? removedKeys = null;
        List<CreditWaiter>? rejected = null;
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            foreach (var pair in _sendStates)
            {
                if (pair.Key.RequestId != requestId)
                    continue;
                var state = pair.Value;
                _sendConnectionCredit = checked(
                    _sendConnectionCredit + (_streamWindow - state.Credit));
                state.Credit = _streamWindow;
                state.AbortException = exception;
                if (state.Completed)
                    (removedKeys ??= []).Add(pair.Key);
            }
            if (removedKeys is not null)
            {
                for (var index = 0; index < removedKeys.Count; index++)
                    _sendStates.Remove(removedKeys[index]);
            }

            var node = _waiters.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key.RequestId == requestId)
                {
                    _waiters.Remove(node);
                    node.Value.Node = null;
                    node.Value.Rejection = exception;
                    (rejected ??= []).Add(node.Value);
                }
                node = next;
            }
            ready = AdmitWaiters();
        }

        CompleteReadyWaiters(rejected);
        CompleteReadyWaiters(ready);
    }

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
                state = new ReceiveState(_streamWindow);
                _receiveStates.Add(key, state);
            }

            if (!CanReserve(state.Credit, _receiveConnectionCredit, encodedBytes))
                throw Violation("StreamData exceeds the negotiated receive window.");
            state.Credit -= encodedBytes;
            _receiveConnectionCredit -= encodedBytes;
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
                    _receiveStates.Remove(key);
                return completedDelta;
            }
            if (state.PendingConsumed >= _streamUpdateThreshold)
                return TakePendingCredit(state);
            if (_pendingConnectionConsumed < _connectionUpdateThreshold)
            {
                return 0;
            }

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
                _receiveStates.Remove(key);
            return delta;
        }
    }

    public void Complete(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        CreditWaiter[] waiters;
        lock (_gate)
        {
            if (_terminalException is not null)
                return;
            _terminalException = exception;
            waiters = new CreditWaiter[_waiters.Count];
            var waiterIndex = 0;
            for (var node = _waiters.First; node is not null; node = node.Next)
                waiters[waiterIndex++] = node.Value;
            _waiters.Clear();
            for (var index = 0; index < waiters.Length; index++)
                waiters[index].Node = null;
            _sendStates.Clear();
            _activeSendStreamCount = 0;
            _receiveStates.Clear();
            _consumedCreditUpdates?.Clear();
            _consumedCreditUpdates = null;
        }

        for (var index = 0; index < waiters.Length; index++)
            waiters[index].Completion.TrySetException(exception);
    }

    internal long SendConnectionCredit
    {
        get
        {
            lock (_gate)
                return _sendConnectionCredit;
        }
    }

    internal int ActiveSendStreamCount
    {
        get
        {
            lock (_gate)
                return _activeSendStreamCount;
        }
    }

    internal int RetainedSendStreamCount
    {
        get
        {
            lock (_gate)
                return _sendStates.Count;
        }
    }

    private SendState AddSendState(StreamKey key)
    {
        var state = new SendState(_streamWindow);
        _sendStates.Add(key, state);
        _activeSendStreamCount++;
        return state;
    }

    private bool CanReserve(long streamCredit, long connectionCredit, int encodedBytes)
    {
        var streamAvailable = encodedBytes <= streamCredit ||
            (encodedBytes > _streamWindow && streamCredit == _streamWindow);
        var connectionAvailable = encodedBytes <= connectionCredit ||
            (encodedBytes > _connectionWindow && connectionCredit == _connectionWindow);
        return streamAvailable && connectionAvailable;
    }

    private void Reserve(SendState state, int encodedBytes)
    {
        state.Credit -= encodedBytes;
        _sendConnectionCredit -= encodedBytes;
    }

    private List<CreditWaiter>? AdmitWaiters()
    {
        List<CreditWaiter>? ready = null;
        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var waiter = node.Value;
            SendState? state = null;
            if (!_sendStates.TryGetValue(waiter.Key, out state))
            {
                if (!waiter.CanCreateState)
                {
                    _waiters.Remove(node);
                    waiter.Node = null;
                    waiter.Rejection = CreateStreamClosedException();
                    (ready ??= []).Add(waiter);
                    node = next;
                    continue;
                }
                if (_sendStates.Count >= _maxConcurrentStreams)
                {
                    node = next;
                    continue;
                }
                state = AddSendState(waiter.Key);
            }
            if (state.Completed || state.AbortException is not null)
            {
                _waiters.Remove(node);
                waiter.Node = null;
                waiter.Rejection = state.AbortException ?? CreateStreamClosedException();
                (ready ??= []).Add(waiter);
                node = next;
                continue;
            }
            if (!HasConnectionCredit(_sendConnectionCredit, waiter.EncodedBytes))
                break;
            if (!HasStreamCredit(state.Credit, waiter.EncodedBytes))
            {
                node = next;
                continue;
            }

            Reserve(state, waiter.EncodedBytes);
            _waiters.Remove(node);
            waiter.Node = null;
            (ready ??= []).Add(waiter);
            node = next;
        }
        return ready;
    }

    private bool HasStreamCredit(long credit, int encodedBytes)
        => encodedBytes <= credit || (encodedBytes > _streamWindow && credit == _streamWindow);

    private bool HasConnectionCredit(long credit, int encodedBytes)
        => encodedBytes <= credit || (encodedBytes > _connectionWindow && credit == _connectionWindow);

    private static void CompleteReadyWaiters(List<CreditWaiter>? ready)
    {
        if (ready is null)
            return;
        for (var index = 0; index < ready.Count; index++)
        {
            var waiter = ready[index];
            if (waiter.Rejection is { } rejection)
                waiter.Completion.TrySetException(rejection);
            else
                waiter.Completion.TrySetResult(true);
        }
    }

    private int TakePendingCredit(ReceiveState state)
    {
        var delta = state.PendingConsumed;
        if (delta == 0)
            return 0;
        state.PendingConsumed = 0;
        _pendingConnectionConsumed -= delta;
        return checked((int)delta);
    }

    private int FlushPendingConnectionCredit(StreamKey currentKey)
    {
        var currentCredit = 0;
        List<StreamKey>? completed = null;
        foreach (var pair in _receiveStates)
        {
            var state = pair.Value;
            var credit = TakePendingCredit(state);
            if (credit != 0)
            {
                if (pair.Key == currentKey)
                    currentCredit = credit;
                else
                    (_consumedCreditUpdates ??= new Queue<ConsumedCreditUpdate>())
                        .Enqueue(new ConsumedCreditUpdate(pair.Key, credit));
            }
            if (state.Completed && state.Credit == _streamWindow)
                (completed ??= []).Add(pair.Key);
        }

        if (completed is not null)
        {
            for (var index = 0; index < completed.Count; index++)
                _receiveStates.Remove(completed[index]);
        }

        return currentCredit;
    }

    private void CancelWaiter(CreditWaiter waiter, CancellationToken cancellationToken)
    {
        List<CreditWaiter>? ready;
        lock (_gate)
        {
            if (waiter.Node is null)
                return;
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            ready = AdmitWaiters();
        }
        waiter.Completion.TrySetCanceled(cancellationToken);
        CompleteReadyWaiters(ready);
    }

    private void ValidateEncodedBytes(int encodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedBytes);
        if (encodedBytes > _maxFramePayloadBytes - sizeof(ushort))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Encoded stream item exceeds the {_maxFramePayloadBytes}-byte frame limit.");
        }
    }

    private void ThrowIfTerminated()
    {
        if (_terminalException is { } exception)
            throw exception;
    }

    private static SharpLinkException Violation(string message)
        => new(SharpLinkErrorCode.ProtocolViolation, message);

    private static SharpLinkException CreateStreamClosedException()
        => new(SharpLinkErrorCode.ConnectionClosed, "The stream is closed.");

    private SharpLinkException CreateConcurrentStreamLimitException()
        => new(
            SharpLinkErrorCode.ResourceExhausted,
            $"The session already owns {_maxConcurrentStreams} active flow-controlled streams.");

    private readonly record struct StreamKey(long RequestId, ushort StreamId);

    private readonly record struct ConsumedCreditUpdate(StreamKey Key, int Credit);

    private sealed class SendState(long initialCredit)
    {
        public long Credit = initialCredit;
        public bool Completed;
        public Exception? AbortException;
    }

    private sealed class ReceiveState(long initialCredit)
    {
        public long Credit = initialCredit;
        public long PendingConsumed;
        public bool Completed;
    }

    private sealed class CreditWaiter(
        StreamFlowController owner,
        StreamKey key,
        int encodedBytes,
        bool canCreateState = false)
    {
        public readonly TaskCompletionSource<bool> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StreamFlowController Owner = owner;
        public readonly StreamKey Key = key;
        public readonly int EncodedBytes = encodedBytes;
        public readonly bool CanCreateState = canCreateState;
        public LinkedListNode<CreditWaiter>? Node;
        public Exception? Rejection;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(
                static (state, token) =>
                {
                    var waiter = (CreditWaiter)state!;
                    waiter.Owner.CancelWaiter(waiter, token);
                },
                this);
            await Completion.Task.ConfigureAwait(false);
        }
    }
}
