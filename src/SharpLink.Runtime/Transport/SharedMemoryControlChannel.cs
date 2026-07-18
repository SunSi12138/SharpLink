namespace SharpLink.Runtime;

internal sealed class SharedMemoryControlChannel : IAsyncDisposable
{
    private const byte DataAvailableSignal = 1;
    private const byte SpaceAvailableSignal = 2;
    private const byte CloseSignal = 3;
    private const int DataAvailableBit = 1;
    private const int SpaceAvailableBit = 2;
    private const int CloseBit = 4;

    private readonly PipeStream _stream;
    private readonly SharedMemoryAsyncPulse _outboundWake = new();
    private readonly SharedMemoryAsyncPulse _dataAvailable = new();
    private readonly SharedMemoryAsyncPulse _spaceAvailable = new();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private Exception? _terminalException;
    private int _pendingOutboundSignals;
    private int _closed;
    private int _disposed;

    public SharedMemoryControlChannel(PipeStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _readerTask = RunReaderAsync();
        _writerTask = RunWriterAsync();
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void ThrowIfFaulted()
    {
        var exception = Volatile.Read(ref _terminalException);
        if (exception is SharpLinkException sharpLinkException)
            throw sharpLinkException;
        if (exception is not null)
            throw CreateConnectionClosedException(exception);
    }

    public void SignalDataAvailable()
    {
        QueueSignal(DataAvailableBit, "data");
    }
    public void SignalSpaceAvailable()
    {
        QueueSignal(SpaceAvailableBit, "space");
    }
    public void PulseDataWaiter() => _dataAvailable.Pulse();
    public void PulseSpaceWaiter() => _spaceAvailable.Pulse();

    public ValueTask WaitForDataAsync(CancellationToken cancellationToken)
        => WaitAsync(_dataAvailable, cancellationToken);

    public ValueTask WaitForSpaceAsync(CancellationToken cancellationToken)
        => WaitAsync(_spaceAvailable, cancellationToken);

    private async ValueTask WaitAsync(SharedMemoryAsyncPulse pulse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
            ThrowClosed();
        _ = await pulse.WaitAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
            ThrowClosed();
    }

    private async Task RunReaderAsync()
    {
        var signal = new byte[1];
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(signal).ConfigureAwait(false);
                if (read == 0)
                    break;
                switch (signal[0])
                {
                    case DataAvailableSignal:
                        _dataAvailable.Pulse();
                        break;
                    case SpaceAvailableSignal:
                        _spaceAvailable.Pulse();
                        break;
                    case CloseSignal:
                        return;
                    default:
                        throw new SharpLinkException(
                            SharpLinkErrorCode.ProtocolViolation,
                            "Shared-memory control channel received an unknown signal.");
                }
            }
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _terminalException, ex, null);
        }
        finally
        {
            MarkClosed();
        }
    }

    private async Task RunWriterAsync()
    {
        var signal = new byte[1];
        try
        {
            while (await _outboundWake.WaitAsync().ConfigureAwait(false))
            {
                var pending = Interlocked.Exchange(ref _pendingOutboundSignals, 0);
                if ((pending & DataAvailableBit) != 0)
                    await WriteSignalAsync(DataAvailableSignal, "data").ConfigureAwait(false);
                if ((pending & SpaceAvailableBit) != 0)
                    await WriteSignalAsync(SpaceAvailableSignal, "space").ConfigureAwait(false);
                if ((pending & CloseBit) != 0)
                {
                    await WriteSignalAsync(CloseSignal, kind: null).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _terminalException, ex, null);
        }
        finally
        {
            MarkClosed();
        }

        async ValueTask WriteSignalAsync(byte value, string? kind)
        {
            signal[0] = value;
            await _stream.WriteAsync(signal).ConfigureAwait(false);
            if (kind is not null)
                SharpLinkTelemetry.RecordSharedMemoryNotification(kind);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!IsClosed)
            QueueSignal(CloseBit, kind: null);
        _outboundWake.Complete();
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException || IsExpectedControlClose(ex))
        {
        }

        MarkClosed();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _dataAvailable.Complete();
        _spaceAvailable.Complete();
        _outboundWake.Complete();
    }

    private bool QueueSignal(int bit, string? kind)
    {
        if (IsClosed)
            return false;
        if (kind is not null)
            SharpLinkTelemetry.RecordSharedMemoryNotificationRequest(kind);
        var previous = Interlocked.Or(ref _pendingOutboundSignals, bit);
        var queued = (previous & bit) == 0;
        if (queued)
            _outboundWake.Pulse();
        else if (kind is not null)
            SharpLinkTelemetry.RecordSharedMemoryNotificationCoalesced(kind);
        return queued;
    }

    private static bool IsExpectedControlClose(Exception exception)
        => exception is IOException or ObjectDisposedException or OperationCanceledException or
            InvalidOperationException or SocketException;

    private void ThrowClosed()
    {
        ThrowIfFaulted();
        throw CreateConnectionClosedException();
    }

    private static SharpLinkException CreateConnectionClosedException(Exception? innerException = null)
        => new(SharpLinkErrorCode.ConnectionClosed, "Shared-memory control channel closed.", innerException);
}
