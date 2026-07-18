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
    private readonly Channel<bool> _outboundWake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Channel<bool> _dataAvailable = CreatePulseChannel();
    private readonly Channel<bool> _spaceAvailable = CreatePulseChannel();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
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

    public void SignalDataAvailable()
    {
        if (QueueSignal(DataAvailableBit))
            SharpLinkTelemetry.RecordSharedMemoryNotification("data");
    }
    public void SignalSpaceAvailable()
    {
        if (QueueSignal(SpaceAvailableBit))
            SharpLinkTelemetry.RecordSharedMemoryNotification("space");
    }
    public void PulseDataWaiter() => _dataAvailable.Writer.TryWrite(true);
    public void PulseSpaceWaiter() => _spaceAvailable.Writer.TryWrite(true);

    public ValueTask WaitForDataAsync(CancellationToken cancellationToken)
        => WaitAsync(_dataAvailable.Reader, cancellationToken);

    public ValueTask WaitForSpaceAsync(CancellationToken cancellationToken)
        => WaitAsync(_spaceAvailable.Reader, cancellationToken);

    private async ValueTask WaitAsync(ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
            throw CreateConnectionClosedException();
        _ = await reader.ReadAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
            throw CreateConnectionClosedException();
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
                        _dataAvailable.Writer.TryWrite(true);
                        break;
                    case SpaceAvailableSignal:
                        _spaceAvailable.Writer.TryWrite(true);
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
            await foreach (var _ in _outboundWake.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var pending = Interlocked.Exchange(ref _pendingOutboundSignals, 0);
                if ((pending & DataAvailableBit) != 0)
                    await WriteSignalAsync(DataAvailableSignal).ConfigureAwait(false);
                if ((pending & SpaceAvailableBit) != 0)
                    await WriteSignalAsync(SpaceAvailableSignal).ConfigureAwait(false);
                if ((pending & CloseBit) != 0)
                {
                    await WriteSignalAsync(CloseSignal).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        finally
        {
            MarkClosed();
        }

        async ValueTask WriteSignalAsync(byte value)
        {
            signal[0] = value;
            await _stream.WriteAsync(signal).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!IsClosed)
            QueueSignal(CloseBit);
        _outboundWake.Writer.TryComplete();
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
        _dataAvailable.Writer.TryWrite(true);
        _spaceAvailable.Writer.TryWrite(true);
        _dataAvailable.Writer.TryComplete();
        _spaceAvailable.Writer.TryComplete();
        _outboundWake.Writer.TryComplete();
    }

    private bool QueueSignal(int bit)
    {
        var previous = Interlocked.Or(ref _pendingOutboundSignals, bit);
        _outboundWake.Writer.TryWrite(true);
        return (previous & bit) == 0;
    }

    private static bool IsExpectedControlClose(Exception exception)
        => exception is IOException or ObjectDisposedException or OperationCanceledException or
            InvalidOperationException or SocketException or SharpLinkException;

    private static SharpLinkException CreateConnectionClosedException(Exception? innerException = null)
        => new(SharpLinkErrorCode.ConnectionClosed, "Shared-memory control channel closed.", innerException);

    private static Channel<bool> CreatePulseChannel()
        => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
}
