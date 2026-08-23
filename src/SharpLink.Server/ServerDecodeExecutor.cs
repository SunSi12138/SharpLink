using System.Threading.Channels;

namespace SharpLink.Server;

/// <summary>
/// Persistent bounded worker pool for request decompression. The executor owns only queue/worker
/// lifetime; request, retained-compressed, decode and decoded-byte ownership remain attached to the
/// caller's request permit and work item until provider execution has either completed or been
/// skipped before start.
/// </summary>
internal sealed class ServerDecodeExecutor : IAsyncDisposable
{
    private readonly Channel<ServerDecodeWorkItem> _channel;
    private readonly Task[] _workers;
    private readonly Task _completion;
    private int _completionRequested;
    private int _queueDepth;
    private int _skippedBeforeStart;
    private int _startedWorkItems;

    internal ServerDecodeExecutor(int workerCount, int queueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _channel = Channel.CreateBounded<ServerDecodeWorkItem>(new BoundedChannelOptions(queueCapacity)
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
    /// Number of decode operations waiting for worker service, including writers currently blocked
    /// by the bounded channel. This is intentionally a pending-work count rather than Channel.Count.
    /// </summary>
    internal int QueueDepth => Volatile.Read(ref _queueDepth);

    internal int SkippedBeforeStart => Volatile.Read(ref _skippedBeforeStart);

    /// <summary>
    /// Monotonic count of work items that won the Queued -&gt; Running transition. This is a test and
    /// diagnostics signal for proving RequestLoop routing without relying on timing heuristics.
    /// </summary>
    internal int StartedWorkItems => Volatile.Read(ref _startedWorkItems);

    internal Task Completion => _completion;

    internal ValueTask EnqueueAsync(
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (Volatile.Read(ref _completionRequested) != 0)
        {
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled(cancellationToken)
                : ValueTask.FromException(
                    new InvalidOperationException("The server decode executor is no longer accepting work."));
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

    private async ValueTask EnqueueCoreAsync(
        ServerDecodeWorkItem workItem,
        CancellationToken cancellationToken)
    {
        workItem.EnableQueuedCancellation(cancellationToken);
        Interlocked.Increment(ref _queueDepth);
        var published = false;
        try
        {
            await _channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
            published = true;
            await workItem.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!published)
            {
                workItem.AbandonBeforePublication();
                var remaining = Interlocked.Decrement(ref _queueDepth);
                if (remaining < 0)
                    throw new InvalidOperationException("Server decode queue depth accounting underflowed.");
            }
            if (exception is ChannelClosedException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw;
        }
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var workItem in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var remaining = Interlocked.Decrement(ref _queueDepth);
            if (remaining < 0)
                throw new InvalidOperationException("Server decode queue depth accounting underflowed.");

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
}

/// <summary>
/// One queued decode operation. Cancellation may complete the caller before worker service only if
/// it wins the Queued -&gt; CancelledBeforeStart transition. If a worker wins Queued -&gt; Running, the
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
