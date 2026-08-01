namespace SharpLink.Runtime;

internal sealed class SharedMemoryControlChannel : IAsyncDisposable
{
    private const byte DataAvailableSignal = 1;
    private const byte SpaceAvailableSignal = 2;
    private const byte CloseSignal = 4;
    private const byte DataWaiterArmedSignal = 8;
    private const byte SpaceWaiterArmedSignal = 16;
    private const byte KnownSignals = DataAvailableSignal | SpaceAvailableSignal | CloseSignal |
                                      DataWaiterArmedSignal | SpaceWaiterArmedSignal;
    private const int DataAvailableBit = 1;
    private const int SpaceAvailableBit = 2;
    private const int CloseBit = 4;
    private const int DataWaiterArmedBit = 8;
    private const int SpaceWaiterArmedBit = 16;

    private readonly PipeStream _stream;
    private readonly SharedMemoryAsyncPulse _outboundWake = new();
    private readonly SharedMemoryAsyncPulse _dataAvailable = new();
    private readonly SharedMemoryAsyncPulse _spaceAvailable = new();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private Action? _peerDataWaiterArmedHandler;
    private Action? _peerSpaceWaiterArmedHandler;
    private Exception? _terminalException;
    private int _pendingOutboundSignals;
    private int _pendingPeerDataWaiterArmed;
    private int _pendingPeerSpaceWaiterArmed;
    private int _waiterHandlersRegistered;
    private int _closed;
    private Task? _disposeTask;

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

    public void SignalDataWaiterArmed()
    {
        QueueSignal(DataWaiterArmedBit, "data-waiter-armed");
    }

    public void SignalSpaceWaiterArmed()
    {
        QueueSignal(SpaceWaiterArmedBit, "space-waiter-armed");
    }

    public void PulseDataWaiter() => _dataAvailable.Pulse();
    public void PulseSpaceWaiter() => _spaceAvailable.Pulse();

    public void RegisterPeerWaiterHandlers(
        Action peerDataWaiterArmed,
        Action peerSpaceWaiterArmed)
    {
        ArgumentNullException.ThrowIfNull(peerDataWaiterArmed);
        ArgumentNullException.ThrowIfNull(peerSpaceWaiterArmed);
        if (Interlocked.Exchange(ref _waiterHandlersRegistered, 1) != 0)
            throw new InvalidOperationException("Shared-memory peer waiter handlers are already registered.");

        Volatile.Write(ref _peerDataWaiterArmedHandler, peerDataWaiterArmed);
        Volatile.Write(ref _peerSpaceWaiterArmedHandler, peerSpaceWaiterArmed);
        if (Interlocked.Exchange(ref _pendingPeerDataWaiterArmed, 0) != 0)
            peerDataWaiterArmed();
        if (Interlocked.Exchange(ref _pendingPeerSpaceWaiterArmed, 0) != 0)
            peerSpaceWaiterArmed();
    }

    public ValueTask WaitForDataAsync(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? WaitWithCancellationAsync(_dataAvailable, cancellationToken)
            : WaitWithoutCancellationAsync(_dataAvailable);

    public ValueTask WaitForSpaceAsync(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? WaitWithCancellationAsync(_spaceAvailable, cancellationToken)
            : WaitWithoutCancellationAsync(_spaceAvailable);

    private async ValueTask WaitWithoutCancellationAsync(SharedMemoryAsyncPulse pulse)
    {
        if (IsClosed)
            ThrowClosed();
        _ = await pulse.WaitAsync().ConfigureAwait(false);
        if (IsClosed)
            ThrowClosed();
    }

    private async ValueTask WaitWithCancellationAsync(
        SharedMemoryAsyncPulse pulse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsClosed)
            ThrowClosed();
        using var cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((SharedMemoryAsyncPulse)state!).Pulse(),
            pulse);
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
                var received = signal[0];
                if (received == 0 || (received & ~KnownSignals) != 0)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.ProtocolViolation,
                        "Shared-memory control channel received an unknown signal.");
                }
                if ((received & DataAvailableSignal) != 0)
                    _dataAvailable.Pulse();
                if ((received & SpaceAvailableSignal) != 0)
                    _spaceAvailable.Pulse();
                if ((received & DataWaiterArmedSignal) != 0)
                    DispatchPeerWaiterArmed(
                        ref _peerDataWaiterArmedHandler,
                        ref _pendingPeerDataWaiterArmed);
                if ((received & SpaceWaiterArmedSignal) != 0)
                    DispatchPeerWaiterArmed(
                        ref _peerSpaceWaiterArmedHandler,
                        ref _pendingPeerSpaceWaiterArmed);
                if ((received & CloseSignal) != 0)
                    return;
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
                if (pending == 0)
                    continue;
                await WriteSignalsAsync((byte)pending).ConfigureAwait(false);
                if ((pending & CloseBit) != 0)
                    return;
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

        async ValueTask WriteSignalsAsync(byte value)
        {
            signal[0] = value;
            await _stream.WriteAsync(signal).ConfigureAwait(false);
            if ((value & DataAvailableSignal) != 0)
                SharpLinkTelemetry.RecordSharedMemoryNotification("data");
            if ((value & SpaceAvailableSignal) != 0)
                SharpLinkTelemetry.RecordSharedMemoryNotification("space");
            if ((value & DataWaiterArmedSignal) != 0)
                SharpLinkTelemetry.RecordSharedMemoryNotification("data-waiter-armed");
            if ((value & SpaceWaiterArmedSignal) != 0)
                SharpLinkTelemetry.RecordSharedMemoryNotification("space-waiter-armed");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_outboundWake)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
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
        Exception? cleanupException = null;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(
                cleanupException,
                exception);
        }
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedControlClose(ex))
        {
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(
                cleanupException,
                exception);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
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

    private static void DispatchPeerWaiterArmed(
        ref Action? handlerField,
        ref int pendingField)
    {
        var handler = Volatile.Read(ref handlerField);
        if (handler is not null)
        {
            handler();
            return;
        }

        Volatile.Write(ref pendingField, 1);
        handler = Volatile.Read(ref handlerField);
        if (handler is not null && Interlocked.Exchange(ref pendingField, 0) != 0)
            handler();
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
