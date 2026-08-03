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
/// Keeps completion of a stream-backed reader behind release of its current
/// <see cref="ReadResult"/>. <see cref="PipeReader.CompleteAsync(Exception?)"/> may otherwise
/// return pooled segments while the single protocol consumer is still dispatching a frame.
/// </summary>
internal sealed class ReadOwnershipPipeReader(PipeReader inner) : PipeReader
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _readReleased;
    private Task? _completionTask;
    private bool _readActive;
    private int _completionRequested;

    internal bool CompletionRequested => Volatile.Read(ref _completionRequested) != 0;

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        try
        {
            inner.AdvanceTo(consumed, examined);
        }
        finally
        {
            ReleaseRead();
        }
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

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
            read = inner.ReadAsync(cancellationToken);
        }
        catch
        {
            ReleaseRead();
            throw;
        }

        return read.IsCompletedSuccessfully
            ? read
            : AwaitReadAsync(read);
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
            if (inner.TryRead(out result))
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

    private async ValueTask<ReadResult> AwaitReadAsync(ValueTask<ReadResult> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch
        {
            ReleaseRead();
            throw;
        }
    }

    private async Task CompleteAfterReadReleaseAsync(Task? release, Exception? exception)
    {
        // CompleteAsync can be entered while the state gate is still held. Move transport
        // cancellation to a later turn so an implementation that completes its pending read
        // inline cannot run the consumer's AdvanceTo continuation under this gate.
        await Task.Yield();
        try
        {
            inner.CancelPendingRead();
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex))
        {
        }

        if (release is not null)
            await release.ConfigureAwait(false);
        await inner.CompleteAsync(exception).ConfigureAwait(false);
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
