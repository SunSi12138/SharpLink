using System.Runtime.CompilerServices;

namespace SharpLink.Server;

/// <summary>
/// Persistent bounded worker pool for request decompression. Production callers reserve one queue
/// slot before retaining request bytes. Published work is scheduled round-robin by connection key;
/// decode concurrency and decoded-byte budgets are acquired only after a worker wins the
/// queued-to-running transition.
/// </summary>
internal sealed class ServerDecodeExecutor : IAsyncDisposable
{
    private static readonly object s_compatibilitySchedulingKey = new();

    private readonly Lock _schedulerGate = new();
    private readonly Dictionary<object, ConnectionQueue> _connectionQueues =
        new(ReferenceKeyComparer.Instance);
    private readonly LinkedList<ConnectionQueue> _readyConnections = [];
    private readonly SemaphoreSlim _readySignal = new(0);
    private readonly SemaphoreSlim _compatibilitySlots;
    private readonly CancellationTokenSource _compatibilityStop = new();
    private readonly Task[] _workers;
    private readonly Task _completion;
    private readonly int _queueCapacity;
    private int _completionRequested;
    private int _disposeRequested;
    private int _queueReservations;
    private int _queueDepth;
    private int _skippedBeforeStart;
    private int _startedWorkItems;

    internal ServerDecodeExecutor(int workerCount, int queueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _queueCapacity = queueCapacity;
        _compatibilitySlots = new SemaphoreSlim(queueCapacity, queueCapacity);
        _workers = new Task[workerCount];
        for (var index = 0; index < _workers.Length; index++)
            _workers[index] = Task.Run(WorkerLoopAsync);
        _completion = Task.WhenAll(_workers);
    }

    internal int WorkerCount => _workers.Length;

    /// <summary>
    /// Number of published operations waiting for worker service, plus compatibility-path writers
    /// blocked by queue capacity. Production reserved publication never blocks on scheduler capacity.
    /// </summary>
    internal int QueueDepth => Volatile.Read(ref _queueDepth);

    /// <summary>
    /// Number of production queue slots reserved but not yet handed to a worker. This includes the
    /// short pre-publication interval used to copy/retain a request after scheduler admission.
    /// </summary>
    internal int QueueReservations => Volatile.Read(ref _queueReservations);

    internal int SkippedBeforeStart => Volatile.Read(ref _skippedBeforeStart);

    internal int StartedWorkItems => Volatile.Read(ref _startedWorkItems);

    internal bool IsAccepting => Volatile.Read(ref _completionRequested) == 0;

    internal Task Completion => _completion;

    internal int ScheduledConnectionCount
    {
        get
        {
            lock (_schedulerGate)
                return _connectionQueues.Count;
        }
    }

    /// <summary>
    /// Reserves scheduler capacity before a production request acquires retained/decode/decoded-byte
    /// ownership. Queue reservations remain globally bounded independently from fair scheduling.
    /// </summary>
    internal bool TryReserveQueueSlot(out ServerDecodeQueuePermit? permit)
    {
        permit = null;
        if (Volatile.Read(ref _completionRequested) != 0)
            return false;

        while (true)
        {
            var current = Volatile.Read(ref _queueReservations);
            if (current >= _queueCapacity)
                return false;
            if (Interlocked.CompareExchange(ref _queueReservations, current + 1, current) != current)
                continue;

            if (Volatile.Read(ref _completionRequested) == 0)
            {
                permit = new ServerDecodeQueuePermit(this);
                return true;
            }

            ReleaseQueueReservation();
            return false;
        }
    }

    /// <summary>
    /// Production publication path. A previously reserved slot guarantees that this caller never
    /// waits while owning downstream decode resources. The scheduling key is normally the physical
    /// server connection and is compared by reference identity.
    /// </summary>
    internal ValueTask EnqueueReservedAsync(
        object schedulingKey,
        ServerDecodeQueuePermit queuePermit,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedulingKey);
        ArgumentNullException.ThrowIfNull(queuePermit);
        ArgumentNullException.ThrowIfNull(workItem);
        queuePermit.MarkEnqueued(this);

        var entry = new ServerDecodeQueueEntry(
            schedulingKey,
            workItem,
            queuePermit,
            releaseCompatibilitySlot: false);
        workItem.EnableQueuedCancellation(
            cancellationToken,
            () => RemoveCancelledBeforeStart(entry));

        var published = false;
        var signalWorker = false;
        lock (_schedulerGate)
        {
            if (Volatile.Read(ref _completionRequested) == 0 && !workItem.IsCancelledBeforeStart)
            {
                PublishEntryLocked(entry, out signalWorker);
                published = true;
            }
        }

        if (!published)
        {
            workItem.AbandonBeforePublication();
            queuePermit.Dispose();

            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled(cancellationToken);
            return ValueTask.FromException(new ServerDecodeExecutorClosedException());
        }

        if (signalWorker)
            _readySignal.Release();
        return new ValueTask(workItem.Completion);
    }

    /// <summary>
    /// Compatibility/test publication path retained for executor-local race tests. Production D
    /// dispatch uses <see cref="TryReserveQueueSlot"/> plus the connection-keyed reserved overload.
    /// </summary>
    internal ValueTask EnqueueAsync(
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
        => EnqueueAsync(s_compatibilitySchedulingKey, workItem, cancellationToken);

    /// <summary>Executor-local keyed path used by deterministic fairness tests.</summary>
    internal ValueTask EnqueueAsync(
        object schedulingKey,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedulingKey);
        ArgumentNullException.ThrowIfNull(workItem);
        if (Volatile.Read(ref _completionRequested) != 0)
        {
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new ServerDecodeExecutorClosedException());
        }

        return EnqueueCoreAsync(schedulingKey, workItem, cancellationToken);
    }

    internal void StopAccepting()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) != 0)
            return;

        _compatibilityStop.Cancel();
        _readySignal.Release(_workers.Length);
    }

    internal async ValueTask CompleteAsync()
    {
        StopAccepting();
        await _completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        _compatibilityStop.Dispose();
        _compatibilitySlots.Dispose();
        _readySignal.Dispose();
    }

    internal void ReleaseQueueReservation()
    {
        var remaining = Interlocked.Decrement(ref _queueReservations);
        if (remaining >= 0)
            return;

        Interlocked.Increment(ref _queueReservations);
        throw new InvalidOperationException("Server decode queue reservation accounting underflowed.");
    }

    private async ValueTask EnqueueCoreAsync(
        object schedulingKey,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _queueDepth);
        var slotAcquired = false;
        var published = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _compatibilityStop.Token);
        try
        {
            await _compatibilitySlots.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            slotAcquired = true;

            var entry = new ServerDecodeQueueEntry(
                schedulingKey,
                workItem,
                queuePermit: null,
                releaseCompatibilitySlot: true);
            workItem.EnableQueuedCancellation(
                cancellationToken,
                () => RemoveCancelledBeforeStart(entry));

            var signalWorker = false;
            lock (_schedulerGate)
            {
                if (Volatile.Read(ref _completionRequested) == 0 && !workItem.IsCancelledBeforeStart)
                {
                    PublishEntryLocked(entry, out signalWorker, queueDepthAlreadyOwned: true);
                    published = true;
                }
            }

            if (!published)
            {
                workItem.AbandonBeforePublication();
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw new ServerDecodeExecutorClosedException();
            }

            if (signalWorker)
                _readySignal.Release();
            await workItem.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!published)
        {
            workItem.AbandonBeforePublication();
            if (_compatibilityStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new ServerDecodeExecutorClosedException();
            throw;
        }
        finally
        {
            if (!published)
            {
                if (slotAcquired)
                    _compatibilitySlots.Release();
                DecrementQueueDepth();
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            await _readySignal.WaitAsync().ConfigureAwait(false);

            if (!TryTakeNextEntry(out var entry))
            {
                if (Volatile.Read(ref _completionRequested) != 0 && QueueDepth == 0)
                    return;
                continue;
            }

            ReleaseQueuedOwnership(entry);
            var workItem = entry.WorkItem;
            if (!workItem.TryStart())
            {
                if (!workItem.IsCancelledBeforeStart)
                    throw new InvalidOperationException("Server decode work item entered an invalid queued state.");
                Interlocked.Increment(ref _skippedBeforeStart);
                workItem.CompleteSkippedBeforeStart();
                continue;
            }

            Interlocked.Increment(ref _startedWorkItems);
            await workItem.RunAsync().ConfigureAwait(false);
        }
    }

    private void PublishEntryLocked(
        ServerDecodeQueueEntry entry,
        out bool signalWorker,
        bool queueDepthAlreadyOwned = false)
    {
        if (!_connectionQueues.TryGetValue(entry.SchedulingKey, out var queue))
        {
            queue = new ConnectionQueue(entry.SchedulingKey);
            _connectionQueues.Add(entry.SchedulingKey, queue);
        }

        entry.Owner = queue;
        entry.PendingNode = queue.Pending.AddLast(entry);
        if (!queueDepthAlreadyOwned)
            Interlocked.Increment(ref _queueDepth);

        signalWorker = false;
        if (queue.ReadyNode is null)
        {
            queue.ReadyNode = _readyConnections.AddLast(queue);
            signalWorker = true;
        }
    }

    private bool TryTakeNextEntry(out ServerDecodeQueueEntry entry)
    {
        var signalAnotherWorker = false;
        lock (_schedulerGate)
        {
            while (_readyConnections.First is { } readyNode)
            {
                var queue = readyNode.Value;
                _readyConnections.Remove(readyNode);
                queue.ReadyNode = null;

                if (queue.Pending.First is not { } pendingNode)
                {
                    _connectionQueues.Remove(queue.SchedulingKey);
                    continue;
                }

                entry = pendingNode.Value;
                queue.Pending.Remove(pendingNode);
                entry.PendingNode = null;
                entry.Owner = null;
                DecrementQueueDepth();

                if (queue.Pending.Count == 0)
                {
                    _connectionQueues.Remove(queue.SchedulingKey);
                }
                else
                {
                    queue.ReadyNode = _readyConnections.AddLast(queue);
                    signalAnotherWorker = true;
                }

                goto Found;
            }

            entry = null!;
            return false;
        }

    Found:
        if (signalAnotherWorker)
            _readySignal.Release();
        return true;
    }

    private void RemoveCancelledBeforeStart(ServerDecodeQueueEntry entry)
    {
        var removed = false;
        lock (_schedulerGate)
        {
            var queue = entry.Owner;
            var pendingNode = entry.PendingNode;
            if (queue is null || pendingNode is null || pendingNode.List is null)
                return;

            queue.Pending.Remove(pendingNode);
            entry.PendingNode = null;
            entry.Owner = null;
            DecrementQueueDepth();

            if (queue.Pending.Count == 0)
            {
                if (queue.ReadyNode is { } readyNode && readyNode.List is not null)
                {
                    _readyConnections.Remove(readyNode);
                    queue.ReadyNode = null;
                }
                _connectionQueues.Remove(queue.SchedulingKey);
            }

            removed = true;
        }

        if (!removed)
            return;

        ReleaseQueuedOwnership(entry);
        Interlocked.Increment(ref _skippedBeforeStart);
        entry.WorkItem.CompleteRemovedBeforeStart();
    }

    private void ReleaseQueuedOwnership(ServerDecodeQueueEntry entry)
    {
        if (entry.QueuePermit is not null)
            entry.QueuePermit.Dispose();
        if (entry.ReleaseCompatibilitySlot)
            _compatibilitySlots.Release();
    }

    private void DecrementQueueDepth()
    {
        var remaining = Interlocked.Decrement(ref _queueDepth);
        if (remaining >= 0)
            return;

        Interlocked.Increment(ref _queueDepth);
        throw new InvalidOperationException("Server decode queue depth accounting underflowed.");
    }

    private sealed class ConnectionQueue(object schedulingKey)
    {
        internal object SchedulingKey { get; } = schedulingKey;

        internal LinkedList<ServerDecodeQueueEntry> Pending { get; } = [];

        internal LinkedListNode<ConnectionQueue>? ReadyNode { get; set; }
    }

    private sealed class ServerDecodeQueueEntry(
        object schedulingKey,
        ServerDecodeWorkItem workItem,
        ServerDecodeQueuePermit? queuePermit,
        bool releaseCompatibilitySlot)
    {
        internal object SchedulingKey { get; } = schedulingKey;

        internal ServerDecodeWorkItem WorkItem { get; } = workItem;

        internal ServerDecodeQueuePermit? QueuePermit { get; } = queuePermit;

        internal bool ReleaseCompatibilitySlot { get; } = releaseCompatibilitySlot;

        internal ConnectionQueue? Owner { get; set; }

        internal LinkedListNode<ServerDecodeQueueEntry>? PendingNode { get; set; }
    }

    private sealed class ReferenceKeyComparer : IEqualityComparer<object>
    {
        internal static ReferenceKeyComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// One bounded persistent-executor queue slot. It is acquired before long-lived request retention and
/// released when a worker dequeues the corresponding work, queued cancellation removes it, or
/// publication fails.
/// </summary>
internal sealed class ServerDecodeQueuePermit : IDisposable
{
    private const int Reserved = 0;
    private const int Enqueued = 1;
    private const int Disposed = 2;

    private readonly ServerDecodeExecutor _executor;
    private int _state = Reserved;

    internal ServerDecodeQueuePermit(ServerDecodeExecutor executor)
        => _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    internal void MarkEnqueued(ServerDecodeExecutor executor)
    {
        if (!ReferenceEquals(_executor, executor))
            throw new InvalidOperationException("A decode queue permit cannot move between executors.");
        if (Interlocked.CompareExchange(ref _state, Enqueued, Reserved) != Reserved)
            throw new InvalidOperationException("A decode queue permit can only be enqueued once.");
    }

    public void Dispose()
    {
        var previous = Interlocked.Exchange(ref _state, Disposed);
        if (previous == Disposed)
            return;
        _executor.ReleaseQueueReservation();
    }
}

/// <summary>
/// Signals that decode publication lost the executor Stop/Drain race before provider execution.
/// This is a normal server-lifecycle boundary, not a worker/provider failure.
/// </summary>
internal sealed class ServerDecodeExecutorClosedException : InvalidOperationException
{
    internal ServerDecodeExecutorClosedException()
        : base("The server decode executor is no longer accepting work.")
    {
    }

    internal ServerDecodeExecutorClosedException(Exception innerException)
        : base("The server decode executor is no longer accepting work.", innerException)
    {
    }
}

/// <summary>
/// One queued decode operation. Cancellation may complete the caller before worker service only if
/// it wins the Queued -> CancelledBeforeStart transition. If a worker wins Queued -> Running, the
/// caller remains joined to worker completion so request-owned buffers cannot be released while the
/// provider can still access them.
/// </summary>
internal sealed class ServerDecodeWorkItem
{
    private const int Queued = 0;
    private const int Running = 1;
    private const int CancelledBeforeStart = 2;
    private const int Completed = 3;

    private readonly Func<CancellationToken, ValueTask> _executeAsync;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _queuedCancellationRegistration;
    private CancellationToken _cancellationToken;
    private Action? _cancelledBeforeStart;
    private int _state = Queued;
    private int _cancellationRegistrationEnabled;

    internal ServerDecodeWorkItem(Func<CancellationToken, ValueTask> executeAsync)
        => _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

    internal Task Completion => _completion.Task;

    internal bool IsCancelledBeforeStart
        => Volatile.Read(ref _state) == CancelledBeforeStart;

    internal void EnableQueuedCancellation(
        CancellationToken cancellationToken,
        Action? cancelledBeforeStart = null)
    {
        if (Interlocked.Exchange(ref _cancellationRegistrationEnabled, 1) != 0)
            throw new InvalidOperationException("Queued cancellation can only be enabled once.");

        _cancellationToken = cancellationToken;
        _cancelledBeforeStart = cancelledBeforeStart;
        if (cancellationToken.CanBeCanceled)
        {
            _queuedCancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((ServerDecodeWorkItem)state!).CancelBeforeStart(),
                this);
        }
    }

    internal bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            return false;

        _queuedCancellationRegistration.Dispose();
        return true;
    }

    internal async ValueTask RunAsync()
    {
        if (Volatile.Read(ref _state) != Running)
            throw new InvalidOperationException("Only running decode work can execute provider code.");

        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            await _executeAsync(_cancellationToken).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _completion.TrySetCanceled(_cancellationToken);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            Volatile.Write(ref _state, Completed);
        }
    }

    internal void CompleteSkippedBeforeStart()
    {
        if (Volatile.Read(ref _state) != CancelledBeforeStart)
            throw new InvalidOperationException("Only cancelled queued decode work can be skipped.");
        _queuedCancellationRegistration.Dispose();
        Volatile.Write(ref _state, Completed);
    }

    internal void CompleteRemovedBeforeStart()
    {
        if (Volatile.Read(ref _state) != CancelledBeforeStart)
            throw new InvalidOperationException("Only cancelled queued decode work can be removed.");
        _queuedCancellationRegistration.Unregister();
        Volatile.Write(ref _state, Completed);
    }

    internal void AbandonBeforePublication()
    {
        _queuedCancellationRegistration.Dispose();
        Interlocked.Exchange(ref _state, Completed);
    }

    private void CancelBeforeStart()
    {
        if (Interlocked.CompareExchange(ref _state, CancelledBeforeStart, Queued) != Queued)
            return;
        _completion.TrySetCanceled(_cancellationToken);
        _cancelledBeforeStart?.Invoke();
    }
}
