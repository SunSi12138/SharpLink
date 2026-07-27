using System.Diagnostics;

namespace SharpLink.Runtime;

public sealed partial class RpcSession : IRpcSession
{
    public string Id { get; }
    public SharpLinkRuntimeContext RuntimeContext { get; private set; } = SharpLinkRuntimeContext.Default;
    internal ProtocolV2Capabilities NegotiatedCapabilities { get; set; }
    private int _negotiatedMaxFramePayloadBytes = SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes;
    internal int NegotiatedMaxFramePayloadBytes => Volatile.Read(ref _negotiatedMaxFramePayloadBytes);
    IRpcRuntimeContext IRpcSession.RuntimeContext => RuntimeContext;
    private long _lastActiveTimestamp = Stopwatch.GetTimestamp();
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    internal TimeSpan TimeSinceLastActivity
        => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActiveTimestamp));
    public PipeReader Input { get; }
    private PipeWriter Output { get; }

    private readonly CancellationTokenSource _cts = new();
    private readonly ITransportConnection? _transportConnection;
    internal EndPoint? LocalEndPoint => _transportConnection?.LocalEndPoint;
    internal EndPoint? RemoteEndPoint => _transportConnection?.RemoteEndPoint;
    private SessionTerminal? _terminal;
    private int _cleanupStarted;
    private int _stopped;
    private readonly TaskCompletionSource<bool> _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _transportDisposeGate = new();
    private Task? _transportDisposeTask;

    public IStreamManager StreamManager { get; private set; } = new StreamManager();
    public bool IsConnected => Volatile.Read(ref _terminal) is null &&
                               (_transportConnection is not null || _isConnected());
    private readonly Action _disconnect;
    private readonly Func<bool> _isConnected;

    private readonly Lock _pumpGate = new();
    private readonly RpcSessionFlushOptions? _flushOptions;
    private SendPump? _pump;
    private StreamFlowController? _streamFlowControl;
    private int _activeRequests;
    private int _draining;
    private string _telemetrySide = "unknown";
    private int _telemetryConnectionState;
    private const int TelemetryNotOpened = 0;
    private const int TelemetryOpened = 1;
    private const int TelemetryClosed = 2;
    internal Func<long, long, long, Exception, SharpLinkException>? ServiceExceptionMapper { get; set; }

    internal void MarkActive()
    {
        Volatile.Write(ref _lastActiveTimestamp, Stopwatch.GetTimestamp());
        LastActive = DateTime.UtcNow;
    }

    internal void SetTelemetrySide(string side)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(side);
        _telemetrySide = side;
    }

    internal SharpLinkException MapServiceException(
        long requestId,
        long contractId,
        long methodId,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (ServiceExceptionMapper is { } mapper)
        {
            try
            {
                return mapper(requestId, contractId, methodId, exception);
            }
            catch
            {
                // A mapper is never allowed to break the session write path.
            }
        }
        return exception as SharpLinkException ?? new SharpLinkException(
            SharpLinkErrorCode.Internal,
            "Internal service error.",
            exception);
    }

    public RpcSession(
        string id,
        PipeReader reader,
        PipeWriter writer,
        Action disconnect,
        Func<bool> isConnected,
        RpcSessionFlushOptions? flushOptions = null)
    {
        if (flushOptions is { } configuredFlushOptions)
        {
            RpcSessionFlushOptions.Validate(
                configuredFlushOptions.FlushSizeThreshold,
                configuredFlushOptions.MaxLatency);
        }

        Id = id;
        Input = reader;
        Output = writer;

        _disconnect = disconnect;
        _isConnected = isConnected;
        _flushOptions = flushOptions;
    }

    /// <summary>Creates an RPC session that owns one transport connection.</summary>
    /// <param name="connection">The independently owned transport connection.</param>
    /// <param name="flushOptions">Optional session flush policy.</param>
    public RpcSession(ITransportConnection connection, RpcSessionFlushOptions? flushOptions = null)
        : this(
            (connection ?? throw new ArgumentNullException(nameof(connection))).Id,
            connection.Input,
            connection.Output,
            static () => { },
            static () => true,
            flushOptions)
    {
        _transportConnection = connection;
    }

    /// <summary>Binds the instance-owned runtime context before the session begins RPC I/O.</summary>
    /// <param name="runtimeContext">The context owned by the connecting client or accepting server.</param>
    public void BindRuntimeContext(SharpLinkRuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);
        if (Volatile.Read(ref _terminal) is not null)
            throw GetTerminalException();

        lock (_pumpGate)
        {
            if (_pump is not null)
                throw new InvalidOperationException("Runtime context must be bound before the first outbound frame.");
            RuntimeContext = runtimeContext;
            Volatile.Write(ref _negotiatedMaxFramePayloadBytes, runtimeContext.Protocol.MaxFramePayloadBytes);
            StreamManager = new StreamManager(
                runtimeContext.Concurrency,
                AcceptReceivedStreamBytes,
                OnStreamBytesConsumed,
                OnReceiveStreamCompleted);
        }
    }

    internal void SetNegotiatedMaxFramePayloadBytes(int value)
    {
        if (value < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            value > RuntimeContext.Protocol.MaxFramePayloadBytes)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                $"Negotiated frame limit {value} is outside the local protocol limits.");
        }
        Volatile.Write(ref _negotiatedMaxFramePayloadBytes, value);
    }

    internal IRpcByteBufferWriter RentFrameWriter()
        => RuntimeContext.Buffers.Rent(checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes));

    internal void EnableStreamFlowControl(int streamWindowBytes, int connectionWindowBytes)
    {
        if ((NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) == 0)
            throw new InvalidOperationException("Flow control was not negotiated for this session.");
        var controller = new StreamFlowController(
            streamWindowBytes,
            connectionWindowBytes,
            NegotiatedMaxFramePayloadBytes,
            RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection);
        if (Interlocked.CompareExchange(ref _streamFlowControl, controller, null) is not null)
            throw new InvalidOperationException("Stream flow control is already enabled for this session.");
    }

    internal ValueTask AcquireStreamSendCreditAsync(
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        var controller = Volatile.Read(ref _streamFlowControl);
        return controller is null
            ? ValueTask.CompletedTask
            : controller.AcquireSendCreditAsync(requestId, streamId, encodedBytes, cancellationToken);
    }

    internal void ReturnUnsentStreamCredit(long requestId, ushort streamId, int encodedBytes)
        => Volatile.Read(ref _streamFlowControl)?.ReturnUnsentCredit(requestId, streamId, encodedBytes);

    internal void ApplyWindowUpdate(long requestId, in ProtocolV2WindowUpdate update)
    {
        var controller = Volatile.Read(ref _streamFlowControl) ??
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                "WindowUpdate was received without negotiated flow control.");
        controller.ApplyWindowUpdate(requestId, update.StreamId, checked((int)update.Credit));
    }

    internal void CompleteSendStream(long requestId, ushort streamId, Exception? exception = null)
        => Volatile.Read(ref _streamFlowControl)?.CompleteSendStream(requestId, streamId, exception);

    internal void AbortSendStreams(long requestId, Exception exception)
        => Volatile.Read(ref _streamFlowControl)?.AbortSendStreams(requestId, exception);

    private void AcceptReceivedStreamBytes(long requestId, ushort streamId, int encodedBytes)
        => Volatile.Read(ref _streamFlowControl)?.AcceptReceived(requestId, streamId, encodedBytes);

    private void OnStreamBytesConsumed(long requestId, ushort streamId, int encodedBytes)
    {
        var controller = Volatile.Read(ref _streamFlowControl);
        var credit = controller?.RecordConsumed(requestId, streamId, encodedBytes) ?? 0;
        if (credit != 0)
            TrySendWindowUpdate(requestId, streamId, credit);
        DrainConsumedCreditUpdates(controller);
    }

    private void OnReceiveStreamCompleted(long requestId, ushort streamId)
    {
        var controller = Volatile.Read(ref _streamFlowControl);
        var credit = controller?.FlushConsumed(requestId, streamId) ?? 0;
        if (credit != 0)
            TrySendWindowUpdate(requestId, streamId, credit);
        DrainConsumedCreditUpdates(controller);
    }

    private void DrainConsumedCreditUpdates(StreamFlowController? controller)
    {
        if (controller is null)
            return;
        while (controller.TryTakeConsumedCreditUpdate(out var requestId, out var streamId, out var credit))
            TrySendWindowUpdate(requestId, streamId, credit);
    }

    private void TrySendWindowUpdate(long requestId, ushort streamId, int credit)
    {
        try
        {
            this.SendWindowUpdate(requestId, streamId, credit);
        }
        catch (SharpLinkException exception) when (
            exception.Code is SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.ResourceExhausted)
        {
            Fault(exception);
        }
    }

    internal void SendPacket(IRpcByteBufferWriter packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (Volatile.Read(ref _terminal) is { } terminal)
        {
            RuntimeContext.Buffers.Return(packet);
            throw terminal.Exception;
        }

        try
        {
            packet = PrepareOutboundPacket(packet, CancellationToken.None);
        }
        catch
        {
            RuntimeContext.Buffers.Return(packet);
            throw;
        }
        ValidateOutboundPacketOrReturn(packet, allowEmpty: false);

        var result = GetOrCreatePump().TryEnqueue(new OwnedFrame(packet, forceFlush: false, flushCompletion: null));
        if (result == SendEnqueueResult.Full)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit.");
        }
        if (result == SendEnqueueResult.Closed)
            throw GetTerminalException();
    }

    internal async ValueTask SendPacketAndFlushAsync(
        IRpcByteBufferWriter packet,
        CancellationToken ct = default)
        => await SendPacketAsync(packet, waitForCapacity: true, forceFlush: true, ct).ConfigureAwait(false);

    internal async ValueTask FlushSendQueueAsync(CancellationToken ct = default)
    {
        var marker = RuntimeContext.Buffers.Rent();
        await SendPacketAsync(marker, waitForCapacity: true, forceFlush: true, ct, allowEmpty: true)
            .ConfigureAwait(false);
    }

    internal async ValueTask SendPacketAsync(
        IRpcByteBufferWriter packet,
        bool waitForCapacity,
        bool forceFlush,
        CancellationToken ct = default,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (Volatile.Read(ref _terminal) is { } terminal)
        {
            RuntimeContext.Buffers.Return(packet);
            throw terminal.Exception;
        }

        if (!allowEmpty)
        {
            try
            {
                packet = PrepareOutboundPacket(packet, ct);
            }
            catch
            {
                RuntimeContext.Buffers.Return(packet);
                throw;
            }
        }
        ValidateOutboundPacketOrReturn(packet, allowEmpty);

        var completion = forceFlush
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var frame = new OwnedFrame(packet, forceFlush, completion);
        var pump = GetOrCreatePump();
        var result = waitForCapacity
            ? await pump.EnqueueAsync(frame, ct).ConfigureAwait(false)
            : pump.TryEnqueue(frame);
        if (result == SendEnqueueResult.Full)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit.");
        }
        if (result == SendEnqueueResult.Closed)
            throw GetTerminalException();

        if (completion is not null)
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void ValidateOutboundPacketOrReturn(IRpcByteBufferWriter packet, bool allowEmpty)
    {
        try
        {
            var length = packet.WrittenCount;
            if (length == 0)
            {
                if (allowEmpty)
                    return;
                throw new InvalidOperationException("Only an internal flush marker may be empty.");
            }
            if (length < ProtocolV2Constants.HeaderBytes)
                throw new InvalidOperationException("Outbound frame is shorter than the protocol header.");

            var payloadLength = length - ProtocolV2Constants.HeaderBytes;
            if (payloadLength > NegotiatedMaxFramePayloadBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Outbound frame payload exceeds the negotiated {NegotiatedMaxFramePayloadBytes}-byte limit.");
            }

            var span = packet.WrittenSpan;
            if (span[0] != ProtocolV2Constants.Magic)
                throw new InvalidOperationException("Outbound frame has an invalid protocol magic byte.");
            var encodedPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(1, sizeof(int)));
            if (encodedPayloadLength != payloadLength)
                throw new InvalidOperationException("Outbound frame payload length does not match its header.");
        }
        catch
        {
            RuntimeContext.Buffers.Return(packet);
            throw;
        }
    }

    internal long QueuedSendBytes => Volatile.Read(ref _pump)?.QueuedBytes ?? 0;

    internal int ActiveRequestCount => Volatile.Read(ref _activeRequests);

    internal bool IsDraining => Volatile.Read(ref _draining) != 0;

    internal bool CanAcceptCalls =>
        !IsDraining && IsConnected;

    internal void AddActiveRequest()
    {
        if (!CanAcceptCalls)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The connection is draining.");
        Interlocked.Increment(ref _activeRequests);
        if (!CanAcceptCalls)
        {
            Interlocked.Decrement(ref _activeRequests);
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "The connection is draining.");
        }
    }

    internal void ReleaseActiveRequest()
    {
        var remaining = Interlocked.Decrement(ref _activeRequests);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _activeRequests, 0);
            throw new InvalidOperationException("Connection active request count became negative.");
        }
    }

    internal void MarkDraining()
        => Volatile.Write(ref _draining, 1);

    public event Action? OnConnected;
    public void NotifyConnected()
    {
        if (Volatile.Read(ref _terminal) is not null ||
            Interlocked.CompareExchange(
                ref _telemetryConnectionState,
                TelemetryOpened,
                TelemetryNotOpened) != TelemetryNotOpened)
        {
            return;
        }

        SharpLinkTelemetry.ConnectionOpened(_telemetrySide);
        if (Volatile.Read(ref _terminal) is not null)
        {
            RecordTelemetryConnectionClosed();
            return;
        }
        OnConnected?.Invoke();
    }
    public event Action<Exception?>? OnDisconnected;
    public void NotifyDisconnected(Exception? exception = null)
        => Fault(exception ?? new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport closed."));

    private void Fault(Exception exception)
    {
        var structured = exception as SharpLinkException ??
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport closed.", exception);
        var terminal = new SessionTerminal(SessionTerminalState.Faulted, structured);
        if (Interlocked.CompareExchange(ref _terminal, terminal, null) is not null)
            return;

        RecordTelemetryConnectionClosed();
        _cts.Cancel();
        Volatile.Read(ref _streamFlowControl)?.Complete(structured);
        Volatile.Read(ref _pump)?.Stop();
        CompleteReceiveStreams(structured);
        _ = StartTransportDispose();
        try
        {
            OnDisconnected?.Invoke(structured);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        var stopping = new SessionTerminal(
            SessionTerminalState.Stopping,
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Session is stopping."));
        if (Interlocked.CompareExchange(ref _terminal, stopping, null) is null)
        {
            RecordTelemetryConnectionClosed();
            Volatile.Read(ref _streamFlowControl)?.Complete(stopping.Exception);
            CompleteReceiveStreams(stopping.Exception);
            try
            {
                OnDisconnected?.Invoke(stopping.Exception);
            }
            catch
            {
            }
        }

        if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0)
        {
            await _stoppedTcs.Task.ConfigureAwait(false);
            return;
        }

        Exception? cleanupException = null;
        try
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception exception)
            {
                cleanupException = exception;
            }

            var pump = Volatile.Read(ref _pump);
            try
            {
                pump?.Stop();
                if (pump is not null)
                    await pump.WaitForStopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupException = CombineCleanupExceptions(cleanupException, exception);
            }

            try
            {
                await Output.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException or ArgumentNullException)
            {
            }
            catch (Exception exception)
            {
                cleanupException = CombineCleanupExceptions(cleanupException, exception);
            }

            try
            {
                await Input.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                cleanupException = CombineCleanupExceptions(cleanupException, exception);
            }

            try
            {
                await StartTransportDispose().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupException = CombineCleanupExceptions(cleanupException, exception);
            }
        }
        finally
        {
            Volatile.Write(ref _stopped, 1);
            _cts.Dispose();
            if (cleanupException is null)
                _stoppedTcs.TrySetResult(true);
            else
                _stoppedTcs.TrySetException(cleanupException);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }

    private static Exception CombineCleanupExceptions(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private void CompleteReceiveStreams(Exception exception)
    {
        try
        {
            StreamManager.CompleteAll(exception);
        }
        catch
        {
            // A user dispatcher cleanup failure cannot interrupt terminal transport cleanup.
        }
    }

    private void RecordTelemetryConnectionClosed()
    {
        if (Interlocked.Exchange(ref _telemetryConnectionState, TelemetryClosed) == TelemetryOpened)
            SharpLinkTelemetry.ConnectionClosed(_telemetrySide);
    }

    private Exception GetTerminalException()
        => Volatile.Read(ref _terminal)?.Exception ??
           new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Session is closed.");

    private void ReturnBuffer(IRpcByteBufferWriter writer)
        => RuntimeContext.Buffers.Return(writer);

    private SendPump GetOrCreatePump()
    {
        var pump = Volatile.Read(ref _pump);
        if (pump is not null)
            return pump;

        lock (_pumpGate)
        {
            pump = _pump;
            if (pump is not null)
                return pump;
            if (Volatile.Read(ref _terminal) is not null)
                throw GetTerminalException();

            pump = new SendPump(
                Output,
                RuntimeContext.Options.PerformanceProfile,
                RuntimeContext.FlowControl.MaxSendQueueBytes,
                _flushOptions,
                _cts.Token,
                ReturnBuffer,
                Fault);
            Volatile.Write(ref _pump, pump);
            return pump;
        }
    }

    private Task StartTransportDispose()
    {
        lock (_transportDisposeGate)
        {
            if (_transportDisposeTask is not null)
                return _transportDisposeTask;

            if (_transportConnection is not null)
            {
                try
                {
                    _transportDisposeTask = _transportConnection.DisposeAsync().AsTask();
                }
                catch (Exception ex)
                {
                    _transportDisposeTask = Task.FromException(ex);
                }
            }
            else
            {
                try
                {
                    _disconnect();
                    _transportDisposeTask = Task.CompletedTask;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException or ArgumentException)
                {
                    _transportDisposeTask = Task.CompletedTask;
                }
            }

            return _transportDisposeTask;
        }
    }

    private sealed record SessionTerminal(SessionTerminalState State, SharpLinkException Exception);

    private enum SessionTerminalState
    {
        Faulted,
        Stopping
    }

    private enum SendEnqueueResult
    {
        Accepted,
        Full,
        Closed
    }
}
