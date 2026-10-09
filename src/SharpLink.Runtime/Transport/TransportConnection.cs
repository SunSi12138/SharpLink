using System.Runtime.ExceptionServices;

namespace SharpLink.Runtime;

internal class StreamTransportConnection : ITransportConnection
{
    internal const int ReadBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    public StreamTransportConnection(Stream stream, EndPoint? localEndPoint = null, EndPoint? remoteEndPoint = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Id = Guid.NewGuid().ToString("N");
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Input = new ReadOwnershipPipeReader(PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(bufferSize: ReadBufferBytes, leaveOpen: true)));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public string Id { get; }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public EndPoint? LocalEndPoint { get; }
    public EndPoint? RemoteEndPoint { get; }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Exception? cleanupException = null;
        try
        {
            await CompleteWriterAsync(Output).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }
        try
        {
            await CompleteReaderAsync(Input).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = CombineCleanupExceptions(cleanupException, exception);
        }
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisposeException(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = CombineCleanupExceptions(cleanupException, exception);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    internal static async ValueTask CompleteWriterAsync(PipeWriter writer)
    {
        try
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisposeException(ex))
        {
        }
    }

    internal static async ValueTask CompleteReaderAsync(PipeReader reader)
    {
        try
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisposeException(ex))
        {
        }
    }

    internal static bool IsExpectedDisposeException(Exception ex)
        => ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException or ArgumentException;

    internal static Exception CombineCleanupExceptions(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);
}

/// <summary>
/// Issue #740 <b>Variant D</b> - LOCAL RESEARCH ONLY, never shipped.
/// <para>
/// Ownership and result semantics match the production
/// <c>ReadOwnershipPipeReader</c>: a <see cref="ReadResult"/> must be released before
/// <see cref="PipeReader.CompleteAsync(Exception?)"/> is allowed to complete the inner reader,
/// and a suspended read that faults or cancels must release ownership exactly once.
/// A failed suspended read retains a separate source lease until its result is observed, so a
/// new read cannot invalidate an unpublished or unobserved failure. This lease does not delay
/// transport completion and does not require <see cref="PipeReader.AdvanceTo(SequencePosition)"/>.
/// </para>
/// <para>
/// The difference is <i>how</i> a suspended read is represented. Production returns the result of
/// an <c>async ValueTask&lt;ReadResult&gt;</c> wrapper, so every true suspension allocates a
/// compiler-generated state-machine box (measured: exactly one 248-byte object). Variant D
/// instead makes this reader its own <see cref="IValueTaskSource{T}"/>, reusing one
/// <see cref="ManualResetValueTaskSourceCore{T}"/> notification per reader and forwarding the
/// inner completion through a single cached delegate. Successful default-context await does not
/// allocate per read; explicit context/scheduler registrations and raw queued-callback backlog
/// use separately measured BCL storage. Per-reader setup allocations are reported separately.
/// Both success and failure state are cleared by their single GetResult observation, which
/// invalidates that arm's token even when no later read suspends. Successful buffer ownership
/// remains active until AdvanceTo independently of the consumed source state.
/// </para>
/// </summary>
internal sealed class ReadOwnershipPipeReader : PipeReader, IValueTaskSource<ReadResult>, IThreadPoolWorkItem
{
    private const int ReadStatusMask = 3;
    private const int ReadSourceActiveMask = 4;
    private const int ReadVersionShift = 16;

    private readonly PipeReader _inner;
    private readonly Lock _gate = new();

    /// <summary>Cached once per reader; registering it must not allocate per read.</summary>
    private readonly Action _onInnerCompleted;
    private static readonly ContextCallback CompleteReadInContext =
        static state => ((ReadOwnershipPipeReader)state!).CompleteInnerRead();

    // Ordinary await reuses this notification. Explicit context/scheduler registrations use a
    // BCL Task notification, whose separately measured allocation is outside the zero-allocation
    // ordinary-await path. Neither notification carries the read buffer or read exception.
    // Under _gate, completion
    // detaches it by value before dispatching outside the gate. A reentrant consumer can therefore
    // arm another read without resetting a core whose SetResult/continuation is still on the stack.
    private ManualResetValueTaskSourceCore<bool> _readNotification;
    private ReadResult _readResult;
    private ExceptionDispatchInfo? _readError;
    // A status query validates the active bit and token from the same atomic snapshot. Results,
    // faults, and continuation registration remain protected by _gate; only status is lock-free.
    private int _readState;
    private short _readVersion;
    private bool _readContinuationRegistered;
    private bool _readRunContinuationsAsynchronously;
    private TaskCompletionSource? _readTaskNotification;
    private readonly LateReadNotification _lateNotification = new();
    private ExecutionContext? _queuedPublicationContext;
    private static readonly ContextCallback PublishQueuedInContext =
        static state => ((ReadOwnershipPipeReader)state!).PublishQueuedRead();

    private ValueTaskSourceStatus ReadStatus => (ValueTaskSourceStatus)(_readState & ReadStatusMask);
    private bool ReadSourceActive => (_readState & ReadSourceActiveMask) != 0;

    /// <summary>The inner read being forwarded. Only valid while a suspension is armed.</summary>
    private ValueTask<ReadResult> _pendingInner;
    private ExecutionContext? _readExecutionContext;

    private TaskCompletionSource? _readReleased;
    private Task? _completionTask;
    private bool _readActive;
    private bool _readFaultPending;
    private int _completionRequested;

    internal ReadOwnershipPipeReader(PipeReader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onInnerCompleted = OnInnerReadCompleted;
#if SHARPLINK_ISSUE740_VARIANT_D_ASYNC_CONTINUATIONS
        // Research-only dispatch policy: queue pending notifications even where the production
        // Task-backed wrapper could inline. Measure this separately from the default policy.
        RunContinuationsAsynchronously = true;
#endif
    }

    // Deterministic test seam for the ownership-release / source-publication boundary.
    internal Action? BeforeReadFailurePublication { get; set; }

    internal bool CompletionRequested => Volatile.Read(ref _completionRequested) != 0;

    /// <summary>
    /// Selects whether pending consumer continuations may run inline on the completing thread.
    /// This research switch keeps both scheduling policies available for direct measurement.
    /// </summary>
    internal bool RunContinuationsAsynchronously { get; set; }

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        try
        {
            _inner.AdvanceTo(consumed, examined);
        }
        finally
        {
            ReleaseRead();
        }
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
        => _ = CompleteAsync(exception);

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        lock (_gate)
        {
            if (_completionTask is not null)
                return new ValueTask(_completionTask);

            Volatile.Write(ref _completionRequested, 1);
            Task? release = null;
            if (_readActive)
            {
                _readReleased = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                release = _readReleased.Task;
            }
            _completionTask = CompleteAfterReadReleaseAsync(release, exception);
            return new ValueTask(_completionTask);
        }
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!TryAcquireRead())
        {
            return ValueTask.FromException<ReadResult>(new InvalidOperationException(
                "The transport reader is completing."));
        }

        ValueTask<ReadResult> read;
        try
        {
            read = _inner.ReadAsync(cancellationToken);
        }
        catch
        {
            ReleaseRead();
            throw;
        }

        if (read.IsCompletedSuccessfully)
            return read;

        // TryAcquireRead grants exclusive arming: no current ValueTask has escaped and the inner
        // callback has not been subscribed. CompleteAsync may cancel, but waits for this read's
        // ownership; no second gate is needed. Initialize everything before publishing Pending.
        var version = unchecked(++_readVersion);
        _readContinuationRegistered = false;
        _readRunContinuationsAsynchronously = RunContinuationsAsynchronously;
        _pendingInner = read;
        // The production async wrapper resumes its state machine under the read caller's
        // context, even when a consumer uses UnsafeOnCompleted. Capture has no per-read
        // allocation; clear the field before dispatch so reentrancy cannot retain old state.
        _readExecutionContext = ExecutionContext.Capture();
        Volatile.Write(ref _readState, ((ushort)version << ReadVersionShift) | ReadSourceActiveMask);

        // Capture the version before subscribing: the inner source may complete inline.
        // A precompleted failure must remain synchronously observable and release transport
        // ownership now. Registering on it would introduce an unnecessary queued callback.
        if (read.IsCompleted)
            OnInnerReadCompleted();
        else
            // Match the production wrapper's ConfigureAwait(false): forwarding must not require
            // the ReadAsync caller's SynchronizationContext or TaskScheduler to make progress.
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_onInnerCompleted);

        return new ValueTask<ReadResult>(this, version);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!TryAcquireRead())
        {
            result = default;
            return false;
        }

        try
        {
            if (_inner.TryRead(out result))
                return true;
        }
        catch
        {
            ReleaseRead();
            throw;
        }

        ReleaseRead();
        return false;
    }

    /// <summary>
    /// Consumes the inner completion under the read caller's ExecutionContext, then publishes
    /// through the selected notification policy without running consumer code under the gate.
    /// </summary>
    private void OnInnerReadCompleted()
    {
        var context = _readExecutionContext;
        _readExecutionContext = null;
        if (context is null)
            CompleteInnerRead();
        else
            ExecutionContext.Run(context, CompleteReadInContext, this);
    }

    private void CompleteInnerRead()
    {
        var inner = _pendingInner;
        _pendingInner = default;

        ReadResult result;
        try
        {
            result = inner.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // Transport completion may proceed after the inner failure, but the reusable source
            // still belongs to this read until its consumer observes the fault. In particular,
            // another ReadAsync must not rearm between ReleaseRead and failure publication.
            lock (_gate)
                _readFaultPending = true;
            ReleaseRead();
            BeforeReadFailurePublication?.Invoke();
            PublishRead(default, ExceptionDispatchInfo.Capture(exception));
            return;
        }

        // SetResult can invoke user code inline. A consumer exception is not an inner-read
        // failure, and a consumer may already have advanced and armed the next read.
        PublishRead(result, null);
    }

    private void PublishRead(ReadResult result, ExceptionDispatchInfo? error, bool fromQueue = false)
    {
        ManualResetValueTaskSourceCore<bool> notification = default;
        TaskCompletionSource? taskNotification = null;
        var queuePublication = false;
        lock (_gate)
        {
            _readResult = result;
            _readError = error;
            if (_readContinuationRegistered && _readTaskNotification is null && !fromQueue &&
                (_readRunContinuationsAsynchronously || Task.CurrentId is not null ||
                 SynchronizationContext.Current is { } context && context.GetType() != typeof(SynchronizationContext) ||
                 !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()))
            {
                // Keep the source Pending until this reusable work item dequeues. Consequently
                // GetResult cannot enable a new arm while these fields still belong to the queue.
                _queuedPublicationContext = ExecutionContext.Capture();
                queuePublication = true;
            }
            else
            {
                var status = error is null ? ValueTaskSourceStatus.Succeeded
                    : error.SourceException is OperationCanceledException ? ValueTaskSourceStatus.Canceled
                    : ValueTaskSourceStatus.Faulted;
                Volatile.Write(ref _readState,
                    ((ushort)_readVersion << ReadVersionShift) | ReadSourceActiveMask | (int)status);
                if (!_readContinuationRegistered)
                    return;
                taskNotification = _readTaskNotification;
                _readTaskNotification = null;
                notification = _readNotification;
                _readNotification = default;
            }
        }
        if (queuePublication)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
        else if (taskNotification is not null)
            taskNotification.SetResult();
        else
            DispatchNotification(ref notification);
    }

    void IThreadPoolWorkItem.Execute()
    {
        var context = _queuedPublicationContext;
        _queuedPublicationContext = null;
        if (context is null)
            PublishQueuedRead();
        else
            ExecutionContext.Run(context, PublishQueuedInContext, this);
    }

    private void PublishQueuedRead()
    {
        ReadResult result;
        ExceptionDispatchInfo? error;
        lock (_gate)
        {
            result = _readResult;
            error = _readError;
            _readResult = default;
            _readError = null;
        }
        // The queued payload/context is detached before completed status enables consumption.
        // No reader field is touched after a consumer can advance and rearm.
        PublishRead(result, error, fromQueue: true);
    }

    private static void DispatchNotification(ref ManualResetValueTaskSourceCore<bool> notification)
    {
        try
        {
            notification.SetResult(true);
        }
        catch (Exception exception)
        {
            // This is a consumer/scheduling error, never an inner read failure. Reporting is
            // deliberately outside the reader and cannot release or overwrite a reentrant arm.
            var error = ExceptionDispatchInfo.Capture(exception);
            ThreadPool.QueueUserWorkItem(static captured => captured.Throw(), error, preferLocal: false);
        }
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
    {
        ExceptionDispatchInfo error;
        lock (_gate)
        {
            ValidateReadToken(token);
            if (ReadStatus == ValueTaskSourceStatus.Pending)
                throw new InvalidOperationException("The read has not completed.");
            if (_readError is null)
            {
                var result = _readResult;
                _readResult = default;
                Volatile.Write(ref _readState, 0);
                return result;
            }

            error = _readError;
            _readError = null;
            Volatile.Write(ref _readState, 0);
            _readFaultPending = false;
        }
        // Caller exception filters run before stack unwinding. Throwing under the gate could
        // deadlock a filter that waits for another thread to rearm this now-consumed read.
        error.Throw();
        return default;
    }

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token)
    {
        var state = Volatile.Read(ref _readState);
        if ((state & ReadSourceActiveMask) == 0 || (short)(state >> ReadVersionShift) != token)
            throw new InvalidOperationException("The read token is no longer valid.");
        return (ValueTaskSourceStatus)(state & ReadStatusMask);
    }

    void IValueTaskSource<ReadResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var useTaskNotification = RequiresTaskNotification(flags);
        lock (_gate)
        {
            ValidateReadToken(token);
            if (_readContinuationRegistered)
                throw new InvalidOperationException("Only one continuation is supported per read.");
            _readContinuationRegistered = true;
            if (ReadStatus == ValueTaskSourceStatus.Pending)
            {
                if (useTaskNotification)
                {
                    var options = _readRunContinuationsAsynchronously
                        ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None;
                    _readTaskNotification = new TaskCompletionSource(options);
                    RegisterTaskNotification(_readTaskNotification.Task, continuation, state, flags);
                }
                else
                {
                    // Publication cannot detach this pending core under the gate. Its async
                    // policy is implemented by our reusable publication work item, not a second
                    // allocating queue operation in the core.
                    _readNotification.OnCompleted(continuation, state, _readNotification.Version, flags);
                }
                return;
            }
        }

        if (useTaskNotification || !_lateNotification.TryQueue(continuation, state, flags))
        {
            // Normal await releases the cached slot before it can consume/rearm. A raw consumer
            // may instead poll GetResult while a notification remains queued; preserve every
            // such callback with independent BCL storage rather than overwrite a bounded slot.
            RegisterTaskNotification(Task.CompletedTask, continuation, state, flags);
        }
    }

    private static bool RequiresTaskNotification(ValueTaskSourceOnCompletedFlags flags)
        => (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0 ||
            (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0 &&
            (SynchronizationContext.Current is { } context && context.GetType() != typeof(SynchronizationContext) ||
             TaskScheduler.Current != TaskScheduler.Default);

    private static void RegisterTaskNotification(
        Task task, Action<object?> continuation, object? state, ValueTaskSourceOnCompletedFlags flags)
    {
        // Keep this capturing adapter in a cold helper: ordinary await must not allocate a
        // display class. The BCL owns scheduler dispatch, context flow and callback containment.
        Action callback = () => continuation(state);
        var awaiter = task.ConfigureAwait((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            .GetAwaiter();
        if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            awaiter.OnCompleted(callback);
        else
            awaiter.UnsafeOnCompleted(callback);
    }

    private sealed class LateReadNotification : IThreadPoolWorkItem
    {
        private ManualResetValueTaskSourceCore<bool> _notification;
        private int _queued;

        internal bool TryQueue(
            Action<object?> continuation, object? state, ValueTaskSourceOnCompletedFlags flags)
        {
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
                return false;
            _notification.OnCompleted(continuation, state, _notification.Version, flags);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
            return true;
        }

        public void Execute()
        {
            var notification = _notification;
            _notification = default;
            Volatile.Write(ref _queued, 0);
            // Release the slot before arbitrary code; an inline rearm can safely reuse it while
            // this detached notification is still on the stack. No slot writes follow dispatch.
            DispatchNotification(ref notification);
        }
    }

    private void ValidateReadToken(short token)
    {
        if (!ReadSourceActive || token != _readVersion)
            throw new InvalidOperationException("The read token is no longer valid.");
    }

    private bool TryAcquireRead()
    {
        lock (_gate)
        {
            if (_completionTask is not null)
                return false;
            if (_readActive || _readFaultPending)
                throw new InvalidOperationException("Concurrent PipeReader reads are not supported.");
            _readActive = true;
            return true;
        }
    }

    private void ReleaseRead()
    {
        TaskCompletionSource? released;
        lock (_gate)
        {
            if (!_readActive)
                return;
            _readActive = false;
            if (ReadSourceActive && ReadStatus == ValueTaskSourceStatus.Succeeded)
            {
                _readResult = default;
                Volatile.Write(ref _readState, 0);
            }
            released = _readReleased;
            _readReleased = null;
        }
        released?.TrySetResult();
    }

    private async Task CompleteAfterReadReleaseAsync(Task? release, Exception? exception)
    {
        // CompleteAsync can be entered while the state gate is still held. Move transport
        // cancellation to a later turn so an implementation that completes its pending read
        // inline cannot run the consumer's AdvanceTo continuation under this gate.
        await Task.Yield();
        try
        {
            _inner.CancelPendingRead();
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex))
        {
        }

        if (release is not null)
            await release.ConfigureAwait(false);
        await _inner.CompleteAsync(exception).ConfigureAwait(false);
    }
}

internal sealed class AnonymousPipeTransportConnection : ITransportConnection
{
    private readonly PipeStream _inputStream;
    private readonly PipeStream _outputStream;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    public AnonymousPipeTransportConnection(PipeStream inputStream, PipeStream outputStream)
    {
        _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        Id = Guid.NewGuid().ToString("N");
        Input = new ReadOwnershipPipeReader(
            PipeReader.Create(inputStream, new StreamPipeReaderOptions(leaveOpen: true)));
        Output = PipeWriter.Create(outputStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public string Id { get; }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Exception? cleanupException = null;
        try
        {
            await StreamTransportConnection.CompleteWriterAsync(Output).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }
        try
        {
            await StreamTransportConnection.CompleteReaderAsync(Input).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }
        try
        {
            await _outputStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }

        try
        {
            await _inputStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }
}
