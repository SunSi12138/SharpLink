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
/// Semantics are intended to be identical to the production
/// <c>ReadOwnershipPipeReader</c>: a <see cref="ReadResult"/> must be released before
/// <see cref="PipeReader.CompleteAsync(Exception?)"/> is allowed to complete the inner reader,
/// and a suspended read that faults or cancels must release ownership exactly once.
/// </para>
/// <para>
/// The difference is <i>how</i> a suspended read is represented. Production returns the result of
/// an <c>async ValueTask&lt;ReadResult&gt;</c> wrapper, so every true suspension allocates a
/// compiler-generated state-machine box (measured: exactly one 248-byte object). Variant D
/// instead makes this reader its own <see cref="IValueTaskSource{T}"/>, reusing one
/// <see cref="ManualResetValueTaskSourceCore{T}"/> per reader and forwarding the inner
/// completion through a single cached delegate, so a suspension allocates nothing.
/// </para>
/// </summary>
internal sealed class ReadOwnershipPipeReader : PipeReader, IValueTaskSource<ReadResult>
{
    private readonly PipeReader _inner;
    private readonly Lock _gate = new();

    /// <summary>Cached once per reader; registering it must not allocate per read.</summary>
    private readonly Action _onInnerCompleted;

    private ManualResetValueTaskSourceCore<ReadResult> _readSource;

    /// <summary>The inner read being forwarded. Only valid while a suspension is armed.</summary>
    private ValueTask<ReadResult> _pendingInner;

    private TaskCompletionSource? _readReleased;
    private Task? _completionTask;
    private bool _readActive;
    private int _completionRequested;

    internal ReadOwnershipPipeReader(PipeReader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onInnerCompleted = OnInnerReadCompleted;
#if SHARPLINK_ISSUE740_VARIANT_D_ASYNC_CONTINUATIONS
        // Match the production wrapper's Task-like scheduling instead of resuming the consumer
        // inline on whichever thread completed the inner read.
        _readSource.RunContinuationsAsynchronously = true;
#endif
    }

    internal bool CompletionRequested => Volatile.Read(ref _completionRequested) != 0;

    /// <summary>
    /// Mirrors the continuation policy of the production wrapper. The compiler-generated
    /// <c>AsyncValueTaskMethodBuilder</c> box used by production behaves like a <see cref="Task"/>
    /// (continuations are not run inline on the completing thread); this switch exists so the
    /// prototype can be measured both ways.
    /// </summary>
    internal bool RunContinuationsAsynchronously
    {
        get => _readSource.RunContinuationsAsynchronously;
        set => _readSource.RunContinuationsAsynchronously = value;
    }

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

        // Arm the reusable completion owner. TryAcquireRead guarantees no other arm is outstanding,
        // so Reset() here cannot invalidate a live continuation. The version is captured before the
        // inner continuation is registered: if the inner read has already completed by the time
        // UnsafeOnCompleted runs it invokes OnInnerReadCompleted inline, and a consumer that
        // observes that inline completion may legally re-arm this reader before ReadAsync returns.
        _readSource.Reset();
        var version = _readSource.Version;
        _pendingInner = read;
        read.GetAwaiter().UnsafeOnCompleted(_onInnerCompleted);

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
    /// Forwards the inner completion to the reused source. Runs on whichever thread completed the
    /// inner read, exactly like the continuation of the production wrapper's <c>await</c>.
    /// </summary>
    private void OnInnerReadCompleted()
    {
        var inner = _pendingInner;
        _pendingInner = default;

        try
        {
            _readSource.SetResult(inner.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            // Same ordering as production: ownership is released before the failure is published.
            ReleaseRead();
            _readSource.SetException(exception);
        }
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token) => _readSource.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _readSource.GetStatus(token);

    void IValueTaskSource<ReadResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _readSource.OnCompleted(continuation, state, token, flags);

    private bool TryAcquireRead()
    {
        lock (_gate)
        {
            if (_completionTask is not null)
                return false;
            if (_readActive)
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
