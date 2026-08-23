using System.Threading.Channels;

namespace SharpLink.Server;

/// <summary>
/// Persistent bounded worker pool for request decompression. Production callers reserve one queue
/// slot before retaining request bytes. Decode concurrency and decoded-byte budgets are acquired only
/// after a worker wins the queued-to-running transition.
/// </summary>
internal sealed class ServerDecodeExecutor : IAsyncDisposable
{
    private readonly Channel<ServerDecodeQueueEntry> _channel;
    private readonly Task[] _workers;
    private readonly Task _completion;
    private readonly int _queueCapacity;
    private int _completionRequested;
    private int _queueReservations;
    private int _queueDepth;
    private int _skippedBeforeStart;
    private int _startedWorkItems;

    internal ServerDecodeExecutor(int workerCount, int queueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _queueCapacity = queueCapacity;
        _channel = Channel.CreateBounded<ServerDecodeQueueEntry>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = workerCount == 1,
            SingleWriter = false
        });
        _workers = new Task[workerCount];
        for (var index = 0; index < _workers.Length; index++)
            _workers[index] = Task.Run(WorkerLoopAsync);
        _completion = Task.WhenAll(_workers);
    }

    internal int WorkerCount => _workers.Length;

    /// <summary>
    /// Number of published operations waiting for worker service, plus compatibility-path writers
    /// blocked by the bounded channel. Production reserved publication does not block on channel
    /// capacity because a queue slot is acquired first.
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

    /// <summary>
    /// Reserves scheduler capacity before a production request acquires retained/decode/decoded-byte
    /// ownership. Queue reservations are bounded independently from provider decode concurrency.
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
    /// waits behind the bounded channel while owning downstream decode resources.
    /// </summary>
    internal ValueTask EnqueueReservedAsync(
        ServerDecodeQueuePermit queuePermit,
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queuePermit);
        ArgumentNullException.ThrowIfNull(workItem);
        queuePermit.MarkEnqueued(this);

        workItem.EnableQueuedCancellation(cancellationToken);
        Interlocked.Increment(ref _queueDepth);
        if (_channel.Writer.TryWrite(new ServerDecodeQueueEntry(workItem, queuePermit)))
            return new ValueTask(workItem.Completion);

        workItem.AbandonBeforePublication();
        DecrementQueueDepth();
        queuePermit.Dispose();

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        if (Volatile.Read(ref _completionRequested) != 0)
            return ValueTask.FromException(new ServerDecodeExecutorClosedException());

        return ValueTask.FromException(new InvalidOperationException(
            "A reserved server decode queue slot could not be published to the bounded channel."));
    }

    /// <summary>
    /// Compatibility/test publication path retained for executor-local race tests. Production D
    /// dispatch uses <see cref="TryReserveQueueSlot"/> plus <see cref="EnqueueReservedAsync"/>.
    /// </summary>
    internal ValueTask EnqueueAsync(
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (Volatile.Read(ref _completionRequested) != 0)
        {
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(new ServerDecodeExecutorClosedException());
        }

        return EnqueueCoreAsync(workItem, cancellationToken);
    }

    internal void StopAccepting()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) == 0)
            _channel.Writer.TryComplete();
    }

    internal async ValueTask CompleteAsync()
    {
        StopAccepting();
        await _completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => CompleteAsync();

    internal void ReleaseQueueReservation()
    {
        var remaining = Interlocked.Decrement(ref _queueReservations);
        if (remaining >= 0)
            return;

        Interlocked.Increment(ref _queueReservations);
        throw new InvalidOperationException("Server decode queue reservation accounting underflowed.");
    }

    private async ValueTask EnqueueCoreAsync(
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        workItem.EnableQueuedCancellation(cancellationToken);
        Interlocked.Increment(ref _queueDepth);
        var published = false;
        try
        {
            await _channel.Writer.WriteAsync(
                new ServerDecodeQueueEntry(workItem, null),
                cancellationToken).ConfigureAwait(false);
            published = true;
            await workItem.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!published)
            {
                workItem.AbandonBeforePublication();
                DecrementQueueDepth();

                if (exception is ChannelClosedException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    throw new ServerDecodeExecutorClosedException(exception);
                }
            }
            throw;
        }
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            DecrementQueueDepth();
            entry.QueuePermit?.Dispose();

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

    private void DecrementQueueDepth()
    {
        var remaining = Interlocked.Decrement(ref _queueDepth);
        if (remaining >= 0)
            return;

        Interlocked.Increment(ref _queueDepth);
        throw new InvalidOperationException("Server decode queue depth accounting underflowed.");
    }

    private readonly record struct ServerDecodeQueueEntry(
        ServerDecodeWorkItem WorkItem,
        ServerDecodeQueuePermit? QueuePermit);
}

/// <summary>
/// One bounded persistent-executor queue slot. It is acquired before long-lived request retention and
/// released when a worker dequeues the corresponding work or publication fails.
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
    private int _state = Queued;
    private int _cancellationRegistrationEnabled;

    internal ServerDecodeWorkItem(Func<CancellationToken, ValueTask> executeAsync)
        => _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

    internal Task Completion => _completion.Task;

    internal bool IsCancelledBeforeStart
        => Volatile.Read(ref _state) == CancelledBeforeStart;

    internal void EnableQueuedCancellation(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _cancellationRegistrationEnabled, 1) != 0)
            throw new InvalidOperationException("Queued cancellation can only be enabled once.");

        _cancellationToken = cancellationToken;
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
    }
}
