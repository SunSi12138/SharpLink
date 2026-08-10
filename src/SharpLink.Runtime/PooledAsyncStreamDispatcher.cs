namespace SharpLink.Runtime;

/// <summary>Decodes a single-consumer RPC stream into a pooled asynchronous enumerator.</summary>
/// <typeparam name="T">The decoded stream item type.</typeparam>
/// <remarks>Dispose the enumerator to release buffered items and return the dispatcher to its pool.</remarks>
public sealed class PooledAsyncStreamDispatcher<T> :
    IStreamConsumptionAwareDispatcher,
    IStreamDispatchLease,
    IAsyncEnumerable<T>,
    IAsyncEnumerator<T>,
    IValueTaskSource<bool>
{
    private static readonly ConcurrentStack<PooledAsyncStreamDispatcher<T>> Pool = [];
    private static int s_retainedCount;

    // 仅用于 WaitSource 的一致性（不是热路径锁）
    private readonly Lock _waitGate = new();

    // Only dispatch-state paths need serialization. The common no-state terminal path must stay
    // allocation-free and avoid this lock; it is the lifecycle measured by the pool benchmark.
    private readonly Lock _dispatchStateGate = new();

    private ManualResetValueTaskSourceCore<bool> _waitSource;

    // Growable SPSC segments avoid copying a ring while the consumer is reading it.
    private BufferSegment _firstSegment = new(16);
    private BufferSegment _consumerSegment;
    private BufferSegment _producerSegment;
    private readonly ConcurrentStack<BufferSegment> _freeSegments = [];
    private int _consumerIndex;
    private int _producerIndex;
    private int _totalCapacity = 16;
    private int _bufferedCount;

    // 0 = 无信号，1 = 有信号（WaitForData 的快路径）
    private int _signalState;

    // 0 = 没 waiter，1 = 有 waiter（Interlocked 管理，避免混用同步手段）
    private int _waiterState;

    // 重要状态：用 Volatile 读写对称（审核 #3）
    private bool _completed;
    private bool _disposed;
    // Disposal completion is keyed by the disposing lease state. The final state is intentionally
    // retained across Reset so a delayed old-generation contender can identify that its disposal
    // already completed without observing fields from a later rental.
    private long _disposeFinalizedLeaseState;
    private DisposeCompletion? _disposeCompletion;
    private TaskCompletionSource? _remoteTerminalPublication;
    private Action? _beforeConcurrentDisposeCompletionInstallForTests;
    private int _terminalDispatchStateClosed;
    // 0 = no remote terminal, 1 = terminal publication still owns dispatch-state close,
    // 2 = remote terminal publication is complete.
    private int _remoteTerminalPublicationState;

    // GetAsyncEnumerator 原子防御（审核 #4）：0/1
    private int _enumeratorTaken;

    // The low two bits encode lease status; the remaining bits form a monotonic generation.
    // This lets a dispatch acquired while Active finish after Dispose transitions the same
    // generation to Disposing, while excluding stale work from a later rental.
    private long _leaseState;
    private int _producerOperations;
    private IStreamDispatchState? _dispatchState;
    private Exception? _error;

    private CancellationToken _enumerationToken;
    private CancellationTokenRegistration _enumerationCancellationRegistration;
    private CancellationToken _additionalEnumerationToken;
    private CancellationTokenRegistration _additionalEnumerationCancellationRegistration;

    private T? _current;
    private IRpcCodec<T>? _codec;
    private bool _payloadNullable;
    private Action<long, ushort, int>? _bytesConsumed;
    private Action<long>? _consumerAbandoned;
    private Func<long, IStreamDispatchState?, ValueTask>? _consumerAbandonedAsync;
    private long _flowControlRequestId;
    private ushort _flowControlStreamId;
    private long _consumerAbandonedRequestId;
    private int _consumerTerminal;

    private const int InitialCapacity = 16;
    private const int ShrinkThreshold = 256;
    private const int MaxBufferedElements = 4096;
    private const int MaxRetainedDispatchers = 1024;
    private const long LeaseStatusMask = 0b11L;
    private const long LeaseInactive = 0b00L;
    private const long LeaseActive = 0b01L;
    private const long LeaseDisposing = 0b10L;

    private PooledAsyncStreamDispatcher()
    {
        _waitSource = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };
        _consumerSegment = _firstSegment;
        _producerSegment = _firstSegment;
    }

    /// <summary>Rents a dispatcher using a codec from the supplied runtime context.</summary>
    /// <param name="enumerationToken">Cancels local stream consumption.</param>
    /// <param name="codecProvider">The required instance-owned codec provider.</param>
    /// <returns>A reset dispatcher that must be asynchronously disposed.</returns>
    public static PooledAsyncStreamDispatcher<T> Rent(
        CancellationToken enumerationToken,
        IRpcCodecProvider codecProvider)
        => Rent(enumerationToken, codecProvider, payloadNullable: false);

    /// <summary>Rents a dispatcher using a codec provider and explicit payload nullability.</summary>
    /// <param name="enumerationToken">Cancels local stream consumption.</param>
    /// <param name="codecProvider">The required instance-owned codec provider.</param>
    /// <param name="payloadNullable">Whether the wire contract permits a null item.</param>
    /// <returns>A reset dispatcher that must be asynchronously disposed.</returns>
    public static PooledAsyncStreamDispatcher<T> Rent(
        CancellationToken enumerationToken,
        IRpcCodecProvider codecProvider,
        bool payloadNullable)
    {
        ArgumentNullException.ThrowIfNull(codecProvider);
        return Rent(enumerationToken, codecProvider.GetCodec<T>(), payloadNullable);
    }

    /// <summary>Rents a dispatcher using a specific item codec.</summary>
    /// <param name="enumerationToken">Cancels local stream consumption.</param>
    /// <param name="codec">The item codec.</param>
    /// <returns>A reset dispatcher that must be asynchronously disposed.</returns>
    public static PooledAsyncStreamDispatcher<T> Rent(
        CancellationToken enumerationToken,
        IRpcCodec<T> codec)
        => Rent(enumerationToken, codec, payloadNullable: false);

    /// <summary>Rents a dispatcher using a specific item codec and explicit payload nullability.</summary>
    /// <param name="enumerationToken">Cancels local stream consumption.</param>
    /// <param name="codec">The item codec.</param>
    /// <param name="payloadNullable">Whether the wire contract permits a null item.</param>
    /// <returns>A reset dispatcher that must be asynchronously disposed.</returns>
    public static PooledAsyncStreamDispatcher<T> Rent(
        CancellationToken enumerationToken,
        IRpcCodec<T> codec,
        bool payloadNullable)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!Pool.TryPop(out var dispatcher))
            dispatcher = new PooledAsyncStreamDispatcher<T>();
        else
            Interlocked.Decrement(ref s_retainedCount);

        dispatcher.Reset(enumerationToken, codec, payloadNullable);
        dispatcher.ActivateLease();
        return dispatcher;
    }

    private void ActivateLease()
    {
        while (true)
        {
            var state = Volatile.Read(ref _leaseState);
            if (GetLeaseStatus(state) != LeaseInactive)
                throw new InvalidOperationException("The stream dispatcher already has an active lease.");
            var activeState = unchecked(state + LeaseActive);
            if (Interlocked.CompareExchange(ref _leaseState, activeState, state) == state)
                return;
        }
    }

    private void Reset(
        CancellationToken enumerationToken,
        IRpcCodec<T> codec,
        bool payloadNullable)
    {
        _enumerationCancellationRegistration.Dispose();
        _enumerationCancellationRegistration = default;
        _additionalEnumerationCancellationRegistration.Dispose();
        _additionalEnumerationCancellationRegistration = default;

        _enumerationToken = enumerationToken;
        _additionalEnumerationToken = default;
        _codec = codec;
        _payloadNullable = payloadNullable;

        _waitSource = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };

        _consumerSegment = _firstSegment;
        _producerSegment = _firstSegment;
        _consumerIndex = 0;
        _producerIndex = 0;
        Volatile.Write(ref _bufferedCount, 0);

        Volatile.Write(ref _signalState, 0);
        Volatile.Write(ref _waiterState, 0);
        Volatile.Write(ref _completed, false);
        Volatile.Write(ref _disposed, false);

        // Do not reset _disposeFinalizedLeaseState here. A delayed caller that observed a
        // previous Disposing state must be able to recognize its old completed lease even after
        // this dispatcher has been returned and rented again. The next disposal overwrites it.

        Volatile.Write(ref _enumeratorTaken, 0);
        Volatile.Write(ref _producerOperations, 0);
        Volatile.Write(ref _dispatchState, null);

        _error = null;
        _current = default;
        _bytesConsumed = null;
        _consumerAbandoned = null;
        _consumerAbandonedAsync = null;
        _flowControlRequestId = 0;
        _flowControlStreamId = 0;
        _consumerAbandonedRequestId = 0;
        Volatile.Write(ref _consumerTerminal, 0);
        Volatile.Write(ref _terminalDispatchStateClosed, 0);
        Volatile.Write(ref _remoteTerminalPublication, null);
        Volatile.Write(ref _beforeConcurrentDisposeCompletionInstallForTests, null);
        Volatile.Write(ref _remoteTerminalPublicationState, 0);
    }

    /// <inheritdoc />
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        => DispatchAsync(payload, checked((int)payload.Length));

    /// <inheritdoc />
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
    {
        if (!TryAcquireDispatch(out var leaseState))
            return RejectedDispatch();
        try
        {
            return DispatchAcquiredAsync(payload, encodedByteCount);
        }
        finally
        {
            ReleaseDispatch(leaseState);
        }
    }

    ValueTask IStreamDispatchLease.DispatchAcquiredAsync(
        ReadOnlySequence<byte> payload,
        int encodedByteCount)
        => DispatchAcquiredAsync(payload, encodedByteCount);

    private ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedByteCount);
        T? item;
        try
        {
            // 反序列化可能很重：尽量让队列/锁不要挡它（这里本来就无锁）
            item = (_codec ?? throw new InvalidOperationException("Stream dispatcher has no codec."))
                .Deserialize(payload);
            if (!_payloadNullable && default(T) is null && item is null)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    "A non-nullable RPC stream item was null.");
            }
        }
        catch
        {
            NotifyBytesConsumed(encodedByteCount);
            throw;
        }

        // 生产者侧：如果已结束/已释放，直接丢弃（读用 Volatile 对称）
        if (Volatile.Read(ref _completed) || Volatile.Read(ref _disposed))
        {
            NotifyBytesConsumed(encodedByteCount);
            return ValueTask.CompletedTask;
        }

        if (Interlocked.Increment(ref _bufferedCount) > MaxBufferedElements)
        {
            Interlocked.Decrement(ref _bufferedCount);
            Complete(new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Stream receive buffer exceeded {MaxBufferedElements} elements."));
            NotifyBytesConsumed(encodedByteCount);
            return ValueTask.CompletedTask;
        }

        Enqueue(item!, encodedByteCount);

        // 审核 #2：扩容路径不再内部 Signal，统一在外层一次
        Signal();
        return ValueTask.CompletedTask;
    }

    void IStreamDispatchLease.BindDispatchState(IStreamDispatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_dispatchStateGate)
        {
            if (Interlocked.CompareExchange(ref _dispatchState, state, null) is not null)
                throw new InvalidOperationException("Stream dispatcher is already registered.");
            if (Volatile.Read(ref _completed) || Volatile.Read(ref _disposed))
                CloseFirstTerminalDispatchState(state);
        }
    }

    void IStreamDispatchLease.OnDispatchesDrained() => TryReturnToPool();

    private bool TryAcquireDispatch(out long leaseState)
    {
        leaseState = Volatile.Read(ref _leaseState);
        if (GetLeaseStatus(leaseState) != LeaseActive)
            return false;

        Interlocked.Increment(ref _producerOperations);
        if (IsSameLeaseGeneration(Volatile.Read(ref _leaseState), leaseState))
            return true;

        Interlocked.Decrement(ref _producerOperations);
        return false;
    }

    private void ReleaseDispatch(long leaseState)
    {
        if (!IsSameLeaseGeneration(Volatile.Read(ref _leaseState), leaseState))
            return;
        if (Interlocked.Decrement(ref _producerOperations) < 0)
            throw new InvalidOperationException("Stream dispatcher producer lease underflowed.");
        TryReturnToPool();
    }

    // A generated server-stream call can be handed to its consumer before an asynchronous
    // WaitForReady registration completes. Keep that unregistered lease out of the pool until
    // registration or failure has reached a terminal state.
    internal long RetainForRegistration()
    {
        if (!TryAcquireDispatch(out var leaseState))
            throw new ObjectDisposedException(
                typeof(PooledAsyncStreamDispatcher<T>).FullName,
                "The stream dispatcher was disposed before registration began.");
        return leaseState;
    }

    internal void ReleaseRegistrationRetention(long leaseState) => ReleaseDispatch(leaseState);

    // This is deliberately reachable only through the existing Runtime → UnitTests friend
    // boundary. It gates a rare second-dispose CAS so the old-generation/re-rent handoff can be
    // tested deterministically without reflection; it is never read by a first disposal.
    internal void SetBeforeConcurrentDisposeCompletionInstallForTests(Action? callback)
        => Volatile.Write(ref _beforeConcurrentDisposeCompletionInstallForTests, callback);

    private static ValueTask RejectedDispatch()
    {
#if DEBUG
        return ValueTask.FromException(new ObjectDisposedException(
            typeof(PooledAsyncStreamDispatcher<T>).FullName,
            "A stream frame targeted a dispatcher after it was returned to the pool."));
#else
        return ValueTask.CompletedTask;
#endif
    }

    /// <inheritdoc />
    public void SetBytesConsumedCallback(
        Action<long, ushort, int>? callback,
        long requestId,
        ushort streamId)
    {
        _bytesConsumed = callback;
        _flowControlRequestId = requestId;
        _flowControlStreamId = streamId;
    }

    /// <summary>Registers a callback that cancels the remote request when the consumer abandons the stream.</summary>
    /// <param name="callback">The abandonment callback, or <see langword="null"/> to disable notification.</param>
    /// <param name="requestId">The request identifier passed to the callback.</param>
    public void SetConsumerAbandonedCallback(Action<long>? callback, long requestId)
    {
        _consumerAbandoned = callback;
        _consumerAbandonedAsync = null;
        _consumerAbandonedRequestId = requestId;
    }

    internal void SetConsumerAbandonedCallback(
        Func<long, IStreamDispatchState?, ValueTask>? callback,
        long requestId)
    {
        _consumerAbandoned = null;
        _consumerAbandonedAsync = callback;
        _consumerAbandonedRequestId = requestId;
    }

    /// <inheritdoc />
    public void Complete(bool isError, string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage;
        Complete(isError
            ? new SharpLinkException(SharpLinkErrorCode.RemoteError, message)
            : null);
    }

    /// <inheritdoc />
    public void Complete(Exception? exception)
    {
        if (Interlocked.CompareExchange(ref _consumerTerminal, 1, 0) != 0)
            return;

        // The no-state path is the regular server-stream lifecycle. Keep it free of locks and
        // allocations; a consumer that catches the narrow remote-publication race joins through
        // the lazy completion below instead of observing a partially published terminal state.
        Volatile.Write(ref _remoteTerminalPublicationState, 1);
        _error = exception;
        Volatile.Write(ref _completed, true);
        try
        {
            var dispatchState = Volatile.Read(ref _dispatchState);
            if (dispatchState is null)
            {
                Signal();
                return;
            }

            lock (_dispatchStateGate)
            {
                if (Volatile.Read(ref _dispatchState) is { } boundDispatchState)
                    CloseFirstTerminalDispatchState(boundDispatchState);
                Signal();
            }
        }
        finally
        {
            // Publish only after the first remote Close and Signal are complete. A racing
            // consumer DisposeAsync then performs its own second Close in sequence.
            Volatile.Write(ref _remoteTerminalPublicationState, 2);
            Volatile.Read(ref _remoteTerminalPublication)?.TrySetResult();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // 审核 #4：原子防御，避免极端并发双进入
        if (Interlocked.CompareExchange(ref _enumeratorTaken, 1, 0) != 0)
            throw new InvalidOperationException("Only single consumer is supported.");

        if (cancellationToken.CanBeCanceled)
        {
            if (!_enumerationToken.CanBeCanceled)
                _enumerationToken = cancellationToken;
            else if (_enumerationToken != cancellationToken)
                _additionalEnumerationToken = cancellationToken;
        }

        _enumerationCancellationRegistration = _enumerationToken.CanBeCanceled
            ? _enumerationToken.UnsafeRegister(static state => ((PooledAsyncStreamDispatcher<T>)state!).Signal(), this)
            : default;
        _additionalEnumerationCancellationRegistration = _additionalEnumerationToken.CanBeCanceled
            ? _additionalEnumerationToken.UnsafeRegister(static state => ((PooledAsyncStreamDispatcher<T>)state!).Signal(), this)
            : default;

        return this;
    }

    /// <inheritdoc />
    public T Current => _current!;

    /// <inheritdoc />
    public async ValueTask<bool> MoveNextAsync()
    {
        while (true)
        {
            ThrowIfEnumerationCanceled();

            if (TryDequeue(out var value, out var encodedByteCount))
            {
                _current = value;
                NotifyBytesConsumed(encodedByteCount);

                // 如果已经 complete 且队列空且已 Dispose，则回收
                if (Volatile.Read(ref _completed) && IsEmpty() && Volatile.Read(ref _disposed))
                    TryReturnToPool();

                return true;
            }

            // 没取到：如果已完成则结束（并抛错误）
            if (Volatile.Read(ref _completed))
            {
                if (Volatile.Read(ref _bufferedCount) != 0 ||
                    Volatile.Read(ref _producerOperations) != 0 ||
                    Volatile.Read(ref _dispatchState)?.HasActiveDispatches == true)
                {
                    await Task.Yield();
                    continue;
                }

                var err = _error;
                if (err is not null)
                    throw err;

                return false;
            }

            ThrowIfEnumerationCanceled();
            await WaitForDataAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        var activeLeaseState = Volatile.Read(ref _leaseState);
        var leaseStatus = GetLeaseStatus(activeLeaseState);
        if (leaseStatus == LeaseInactive)
            return ValueTask.CompletedTask;

        if (leaseStatus == LeaseDisposing)
            return AwaitConcurrentDisposeAsync(activeLeaseState);

        if (leaseStatus != LeaseActive)
            return ValueTask.CompletedTask;

        var disposingLeaseState = unchecked(activeLeaseState + 1);
        var observedLeaseState = Interlocked.CompareExchange(
            ref _leaseState,
            disposingLeaseState,
            activeLeaseState);
        if (observedLeaseState != activeLeaseState)
        {
            // A contender from an old lease must never retry against a newly rented generation.
            return GetLeaseStatus(observedLeaseState) == LeaseDisposing &&
                IsSameLeaseGeneration(observedLeaseState, activeLeaseState)
                ? AwaitConcurrentDisposeAsync(observedLeaseState)
                : ValueTask.CompletedTask;
        }

        var terminal = Volatile.Read(ref _consumerTerminal);
        if (terminal == 0 && Interlocked.CompareExchange(ref _consumerTerminal, 2, 0) == 0)
        {
            _error ??= new OperationCanceledException(
                "The response stream consumer stopped before remote completion.");
            Volatile.Write(ref _completed, true);
            return MarkDisposedAndFinishDisposeAsync(
                notifyConsumerAbandoned: true,
                remoteTerminalPublished: false,
                disposingLeaseState: disposingLeaseState);
        }

        terminal = Volatile.Read(ref _consumerTerminal);
        if (terminal == 1 && Volatile.Read(ref _remoteTerminalPublicationState) != 2)
            return AwaitRemoteTerminalPublicationAndFinishDisposeAsync(disposingLeaseState);

        return MarkDisposedAndFinishDisposeAsync(
            notifyConsumerAbandoned: false,
            remoteTerminalPublished: terminal == 1,
            disposingLeaseState: disposingLeaseState);
    }

    private ValueTask AwaitConcurrentDisposeAsync(long disposingLeaseState)
    {
        if (Volatile.Read(ref _disposeFinalizedLeaseState) == disposingLeaseState ||
            Volatile.Read(ref _leaseState) != disposingLeaseState)
        {
            return ValueTask.CompletedTask;
        }

        while (true)
        {
            if (Volatile.Read(ref _disposeFinalizedLeaseState) == disposingLeaseState ||
                Volatile.Read(ref _leaseState) != disposingLeaseState)
            {
                return ValueTask.CompletedTask;
            }

            var existing = Volatile.Read(ref _disposeCompletion);
            if (existing is not null)
            {
                if (existing.LeaseState != disposingLeaseState)
                {
                    // A delayed old-generation contender can lose the return race and install
                    // after this instance has already been rented again. Remove only the stale
                    // holder it observed, then let the current generation establish its own.
                    Interlocked.CompareExchange(ref _disposeCompletion, null, existing);
                    continue;
                }

                if (Volatile.Read(ref _disposeFinalizedLeaseState) == disposingLeaseState ||
                    Volatile.Read(ref _leaseState) != disposingLeaseState)
                {
                    existing.Completion.TrySetResult();
                }

                return new ValueTask(existing.Completion.Task);
            }

            var created = new DisposeCompletion(disposingLeaseState);
            Volatile.Read(ref _beforeConcurrentDisposeCompletionInstallForTests)?.Invoke();
            if (Interlocked.CompareExchange(ref _disposeCompletion, created, null) is not null)
                continue;

            if (Volatile.Read(ref _disposeFinalizedLeaseState) == disposingLeaseState ||
                Volatile.Read(ref _leaseState) != disposingLeaseState)
            {
                created.Completion.TrySetResult();
                Interlocked.CompareExchange(ref _disposeCompletion, null, created);
            }

            return new ValueTask(created.Completion.Task);
        }
    }

    private async ValueTask AwaitRemoteTerminalPublicationAndFinishDisposeAsync(long disposingLeaseState)
    {
        await WaitForRemoteTerminalPublicationAsync().ConfigureAwait(false);
        await MarkDisposedAndFinishDisposeAsync(
            notifyConsumerAbandoned: false,
            remoteTerminalPublished: true,
            disposingLeaseState: disposingLeaseState).ConfigureAwait(false);
    }

    private ValueTask WaitForRemoteTerminalPublicationAsync()
    {
        if (Volatile.Read(ref _remoteTerminalPublicationState) == 2)
            return ValueTask.CompletedTask;

        var completion = Volatile.Read(ref _remoteTerminalPublication);
        if (completion is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = Interlocked.CompareExchange(ref _remoteTerminalPublication, created, null) ?? created;
        }

        if (Volatile.Read(ref _remoteTerminalPublicationState) == 2)
            completion.TrySetResult();

        return new ValueTask(completion.Task);
    }

    private ValueTask MarkDisposedAndFinishDisposeAsync(
        bool notifyConsumerAbandoned,
        bool remoteTerminalPublished,
        long disposingLeaseState)
    {
        Volatile.Write(ref _disposed, true);
        _current = default;

        var dispatchState = Volatile.Read(ref _dispatchState);
        if (dispatchState is null)
        {
            Signal();
        }
        else
        {
            lock (_dispatchStateGate)
            {
                if (Volatile.Read(ref _dispatchState) is { } boundDispatchState)
                {
                    if (remoteTerminalPublished)
                    {
                        // A remote terminal close is already published. Consumer abandonment
                        // intentionally performs the second close only after that close ended.
                        boundDispatchState.Close();
                    }
                    else
                    {
                        CloseFirstTerminalDispatchState(boundDispatchState);
                    }
                }
                Signal();
            }
        }

        // Stop StreamManager from accepting another frame before observing that the
        // already-acquired dispatches drained. This makes the final WindowUpdate and
        // Cancel ordering deterministic without adding synchronization to normal reads.
        if (dispatchState?.HasActiveDispatches == true)
        {
            return AwaitDispatchesAndFinishDisposeAsync(
                notifyConsumerAbandoned,
                dispatchState,
                disposingLeaseState);
        }

        return FinishDisposeAsync(notifyConsumerAbandoned, dispatchState, disposingLeaseState);
    }

    // Must be called while _dispatchStateGate is held. It lets a late Bind race the first
    // terminal publication without turning a state-free terminal path into a locked path.
    private void CloseFirstTerminalDispatchState(IStreamDispatchState dispatchState)
    {
        if (Interlocked.CompareExchange(ref _terminalDispatchStateClosed, 1, 0) == 0)
            dispatchState.Close();
    }

    private async ValueTask AwaitDispatchesAndFinishDisposeAsync(
        bool notifyConsumerAbandoned,
        IStreamDispatchState dispatchState,
        long disposingLeaseState)
    {
        await dispatchState.WaitForDispatchesDrainedAsync().ConfigureAwait(false);
        await FinishDisposeAsync(
            notifyConsumerAbandoned,
            dispatchState,
            disposingLeaseState).ConfigureAwait(false);
    }

    private ValueTask FinishDisposeAsync(
        bool notifyConsumerAbandoned,
        IStreamDispatchState? dispatchState,
        long disposingLeaseState)
    {
        try
        {
            // A dispatch acquired before Close may have published an item or returned
            // receive credit after DisposeAsync began. Drain only after it is quiescent.
            var discardedBytes = 0;
            while (TryDequeue(out _, out var encodedByteCount))
                discardedBytes = checked(discardedBytes + encodedByteCount);
            if (discardedBytes != 0)
                NotifyBytesConsumed(discardedBytes);
            if (notifyConsumerAbandoned)
            {
                if (_consumerAbandonedAsync is { } callback)
                {
                    var completion = callback(_consumerAbandonedRequestId, dispatchState);
                    if (!completion.IsCompletedSuccessfully)
                        return AwaitConsumerAbandonmentAndFinalizeAsync(
                            completion,
                            disposingLeaseState);
                    completion.GetAwaiter().GetResult();
                }
                else
                {
                    _consumerAbandoned?.Invoke(_consumerAbandonedRequestId);
                }
            }
        }
        catch
        {
            FinalizeDispose(disposingLeaseState);
            throw;
        }

        FinalizeDispose(disposingLeaseState);
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitConsumerAbandonmentAndFinalizeAsync(
        ValueTask completion,
        long disposingLeaseState)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            FinalizeDispose(disposingLeaseState);
        }
    }

    private void FinalizeDispose(long disposingLeaseState)
    {
        if (Volatile.Read(ref _disposeFinalizedLeaseState) == disposingLeaseState ||
            Volatile.Read(ref _leaseState) != disposingLeaseState)
        {
            return;
        }

        // Publish finalization before looking for the lazy concurrent-dispose waiter. A waiter
        // installed immediately afterwards observes this state and completes itself, so the
        // common first-dispose path needs neither a lock nor a completion allocation.
        Signal();
        Volatile.Write(ref _disposeFinalizedLeaseState, disposingLeaseState);
        var completion = Volatile.Read(ref _disposeCompletion);
        if (completion?.LeaseState == disposingLeaseState)
            completion.Completion.TrySetResult();

        TryReturnToPool(disposingLeaseState);
    }

    /// <inheritdoc />
    public bool GetResult(short token) => _waitSource.GetResult(token);
    bool IValueTaskSource<bool>.GetResult(short token) => _waitSource.GetResult(token);

    /// <inheritdoc />
    public ValueTaskSourceStatus GetStatus(short token) => _waitSource.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _waitSource.GetStatus(token);

    /// <inheritdoc />
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _waitSource.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _waitSource.OnCompleted(continuation, state, token, flags);

    // --------------------------
    // SPSC segmented buffer primitives
    // --------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enqueue(T item, int encodedByteCount)
    {
        var segment = _producerSegment;
        var index = _producerIndex;
        if (index == segment.Items.Length)
        {
            BufferSegment next;
            if (_freeSegments.TryPop(out var recycled))
            {
                next = recycled;
            }
            else
            {
                var retainedCapacityRemaining = ShrinkThreshold - _totalCapacity;
                var nextCapacity = retainedCapacityRemaining > 0
                    ? Math.Min(segment.Items.Length << 1, retainedCapacityRemaining)
                    : Math.Min(segment.Items.Length << 1, ShrinkThreshold);
                next = new BufferSegment(nextCapacity);
                _totalCapacity += nextCapacity;
            }
            Volatile.Write(ref segment.Next, next);
            _producerSegment = segment = next;
            _producerIndex = index = 0;
        }

        segment.Items[index] = item;
        segment.EncodedByteCounts[index] = encodedByteCount;
        _producerIndex = index + 1;
        Volatile.Write(ref segment.Published, index + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDequeue(out T value, out int encodedByteCount)
    {
        while (true)
        {
            var segment = _consumerSegment;
            var index = _consumerIndex;
            var published = Volatile.Read(ref segment.Published);
            if (index < published)
            {
                value = segment.Items[index];
                encodedByteCount = segment.EncodedByteCounts[index];
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    segment.Items[index] = default!;
                segment.EncodedByteCounts[index] = 0;
                _consumerIndex = index + 1;
                Interlocked.Decrement(ref _bufferedCount);
                return true;
            }

            if (index == segment.Items.Length && Volatile.Read(ref segment.Next) is { } next)
            {
                _consumerSegment = next;
                _consumerIndex = 0;
                Volatile.Write(ref segment.Next, null);
                Volatile.Write(ref segment.Published, 0);
                _freeSegments.Push(segment);
                continue;
            }

            value = default!;
            encodedByteCount = 0;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEmpty()
        => Volatile.Read(ref _bufferedCount) == 0;

    // --------------------------
    // Wait/Signal (async)
    // --------------------------

    private ValueTask<bool> WaitForDataAsync()
    {
        // 快路径：已有 signal
        if (Interlocked.Exchange(ref _signalState, 0) == 1)
            return ValueTask.FromResult(true);

        lock (_waitGate)
        {
            if (Interlocked.Exchange(ref _signalState, 0) == 1)
                return ValueTask.FromResult(true);

            // 单消费者下理论上不会重入，但保留防御
            if (Interlocked.CompareExchange(ref _waiterState, 1, 0) != 0)
                throw new InvalidOperationException("Only one waiter is supported.");

            if (Interlocked.Exchange(ref _signalState, 0) == 1)
            {
                Interlocked.Exchange(ref _waiterState, 0);
                return ValueTask.FromResult(true);
            }

            _waitSource.Reset();
            return new ValueTask<bool>(this, _waitSource.Version);
        }
    }

    private void Signal()
    {
        // 设置 signal（如果本来就有，则直接返回）
        if (Interlocked.Exchange(ref _signalState, 1) == 1)
            return;

        // 无 waiter：留给快路径
        if (Volatile.Read(ref _waiterState) == 0)
            return;

        lock (_waitGate)
        {
            if (Interlocked.Exchange(ref _waiterState, 0) == 1)
            {
                Interlocked.Exchange(ref _signalState, 0);
                _waitSource.SetResult(true);
            }
        }
    }

    // --------------------------
    // Pooling (安全策略：必须 completed && empty 才回池)
    // --------------------------

    private void TryReturnToPool()
    {
        var disposingLeaseState = Volatile.Read(ref _leaseState);
        if (GetLeaseStatus(disposingLeaseState) != LeaseDisposing)
            return;
        TryReturnToPool(disposingLeaseState);
    }

    private void TryReturnToPool(long disposingLeaseState)
    {
        if (GetLeaseStatus(disposingLeaseState) != LeaseDisposing ||
            Volatile.Read(ref _leaseState) != disposingLeaseState)
            return;

        // 关键：不保证消费者 Dispose 后生产者停止 => 必须等 completed 才能安全回收
        if (!Volatile.Read(ref _completed) || !Volatile.Read(ref _disposed) ||
            Volatile.Read(ref _disposeFinalizedLeaseState) != disposingLeaseState || !IsEmpty() ||
            Volatile.Read(ref _producerOperations) != 0 ||
            Volatile.Read(ref _dispatchState) is { } state &&
            (state.HasActiveDispatches || !state.IsDetached))
            return;

        var returnedState = unchecked(disposingLeaseState + 2);
        if (Interlocked.CompareExchange(
                ref _leaseState,
                returnedState,
                disposingLeaseState) != disposingLeaseState)
            return;

        // Close the acquire-vs-return race before clearing lease state.
        var dispatchState = Volatile.Read(ref _dispatchState);
        if (Volatile.Read(ref _producerOperations) != 0 || !IsEmpty() ||
            dispatchState is { } && (dispatchState.HasActiveDispatches || !dispatchState.IsDetached))
        {
            if (Interlocked.CompareExchange(
                    ref _leaseState,
                    disposingLeaseState,
                    returnedState) != returnedState)
                throw new InvalidOperationException("The stream dispatcher return state changed unexpectedly.");
            return;
        }

        _enumerationCancellationRegistration.Dispose();
        _enumerationCancellationRegistration = default;
        _additionalEnumerationCancellationRegistration.Dispose();
        _additionalEnumerationCancellationRegistration = default;
        _enumerationToken = default;
        _additionalEnumerationToken = default;
        _error = null;
        _codec = null;
        _payloadNullable = false;
        _bytesConsumed = null;
        _consumerAbandoned = null;
        _consumerAbandonedAsync = null;
        _flowControlRequestId = 0;
        _flowControlStreamId = 0;
        _consumerAbandonedRequestId = 0;
        Volatile.Write(ref _dispatchState, null);
        Volatile.Write(ref _disposeCompletion, null);
        Volatile.Write(ref _remoteTerminalPublication, null);
        Volatile.Write(ref _beforeConcurrentDisposeCompletionInstallForTests, null);

        // 复位枚举器占用标记
        Volatile.Write(ref _enumeratorTaken, 0);
        _current = default;

        if (_totalCapacity > ShrinkThreshold)
        {
            while (_freeSegments.TryPop(out _))
            {
            }
            _firstSegment = new BufferSegment(InitialCapacity);
            _totalCapacity = InitialCapacity;
        }
        else
        {
            var active = _consumerSegment;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(active.Items);
            Array.Clear(active.EncodedByteCounts);
            Volatile.Write(ref active.Published, 0);
            Volatile.Write(ref active.Next, null);
            foreach (var segment in _freeSegments)
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    Array.Clear(segment.Items);
                Array.Clear(segment.EncodedByteCounts);
                Volatile.Write(ref segment.Published, 0);
                Volatile.Write(ref segment.Next, null);
            }
            _firstSegment = active;
        }
        _consumerSegment = _firstSegment;
        _producerSegment = _firstSegment;
        _consumerIndex = 0;
        _producerIndex = 0;

        Volatile.Write(ref _signalState, 0);
        Volatile.Write(ref _waiterState, 0);
        // 注意：_completed/_disposed 会在下次 Reset 时统一清
        if (Interlocked.Increment(ref s_retainedCount) <= MaxRetainedDispatchers)
        {
            Pool.Push(this);
            return;
        }

        Interlocked.Decrement(ref s_retainedCount);
    }

    internal static int RetainedCountForTests => Volatile.Read(ref s_retainedCount);

    internal int BufferCapacityForTests => _totalCapacity;

    internal bool HasRetainedReferencesForTests
    {
        get
        {
            if (_codec is not null || _bytesConsumed is not null || _consumerAbandoned is not null ||
                _consumerAbandonedAsync is not null ||
                _current is not null || _enumerationToken.CanBeCanceled ||
                _additionalEnumerationToken.CanBeCanceled ||
                !_enumerationCancellationRegistration.Equals(default) ||
                !_additionalEnumerationCancellationRegistration.Equals(default) ||
                Volatile.Read(ref _disposeCompletion) is not null ||
                Volatile.Read(ref _remoteTerminalPublication) is not null)
            {
                return true;
            }

            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return false;
            if (SegmentHasReferences(_firstSegment))
                return true;
            foreach (var segment in _freeSegments)
            {
                if (SegmentHasReferences(segment))
                    return true;
            }
            return false;
        }
    }

    private static bool SegmentHasReferences(BufferSegment segment)
    {
        for (var index = 0; index < segment.Items.Length; index++)
        {
            if (segment.Items[index] is not null)
                return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetLeaseStatus(long leaseState) => leaseState & LeaseStatusMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSameLeaseGeneration(long first, long second)
        => (first & ~LeaseStatusMask) == (second & ~LeaseStatusMask);

    internal static void ClearPoolForTests()
    {
        while (Pool.TryPop(out _))
            Interlocked.Decrement(ref s_retainedCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyBytesConsumed(int encodedByteCount)
        => _bytesConsumed?.Invoke(_flowControlRequestId, _flowControlStreamId, encodedByteCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfEnumerationCanceled()
    {
        _enumerationToken.ThrowIfCancellationRequested();
        if (_additionalEnumerationToken.CanBeCanceled)
            _additionalEnumerationToken.ThrowIfCancellationRequested();
    }

    private sealed class BufferSegment(int capacity)
    {
        internal readonly T[] Items = new T[capacity];
        internal readonly int[] EncodedByteCounts = new int[capacity];
        internal int Published;
        internal BufferSegment? Next;
    }

    private sealed class DisposeCompletion(long leaseState)
    {
        internal long LeaseState { get; } = leaseState;

        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
