namespace SharpLink.Runtime;

/// <summary>Owns protocol state, buffering, flow control, and lifecycle for one RPC transport connection.</summary>
internal sealed partial class RpcSession
{
    internal string Id { get; }
    /// <summary>Gets the instance-owned runtime services used by this session.</summary>
    internal SharpLinkRuntimeContext RuntimeContext { get; }
    internal RpcSessionRole Role { get; }
    private RpcSessionProtocolState _protocolState = RpcSessionProtocolState.Handshaking;
    private int _handshakeCompletionStarted;
    internal NegotiatedSessionOptions? NegotiatedOptions
        => Volatile.Read(ref _protocolState).Options;
    internal ProtocolV2Capabilities NegotiatedCapabilities
        => Volatile.Read(ref _protocolState).Options?.Capabilities ?? ProtocolV2Capabilities.None;
    internal int NegotiatedMaxFramePayloadBytes
        => Volatile.Read(ref _protocolState).Options?.MaxFramePayloadBytes ??
            RuntimeContext.Protocol.MaxFramePayloadBytes;
    internal RpcSessionProtocolPhase ProtocolPhase
        => Volatile.Read(ref _protocolState).Phase;
    internal bool HasStreamFlowControl
        => Volatile.Read(ref _protocolState).FlowController is not null;
    private long _lastActiveTimestamp;
    private long _lastActiveUtcTicks;
    internal DateTime LastActive
    {
        get => new(Volatile.Read(ref _lastActiveUtcTicks), DateTimeKind.Utc);
        set
        {
            Volatile.Write(
                ref _lastActiveUtcTicks,
                value.Kind == DateTimeKind.Local
                    ? value.ToUniversalTime().Ticks
                    : value.Ticks);
        }
    }
    internal TimeSpan TimeSinceLastActivity
        => RuntimeContext.TimeProvider.GetElapsedTime(Volatile.Read(ref _lastActiveTimestamp));
    internal PipeReader Input => _transport.Input;
    private PipeWriter Output => _transport.Output;

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly ITransportConnection _transport;
    internal EndPoint? LocalEndPoint => _transport.LocalEndPoint;
    internal EndPoint? RemoteEndPoint => _transport.RemoteEndPoint;
    private SessionTerminal? _terminal;
    private int _cleanupStarted;
    private int _stopped;
    private readonly TaskCompletionSource<bool> _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _ctsGate = new();
    private bool _ctsCancellationSignaled;
    private readonly Lock _transportDisposeGate = new();
    private Task? _transportDisposeTask;

    internal StreamManager StreamManager { get; }
    internal bool IsConnected => Volatile.Read(ref _terminal) is null;
    internal CancellationToken LifetimeToken => _lifetimeToken;

    private readonly Lock _pumpGate = new();
    private readonly RpcSessionFlushOptions? _flushOptions;
    private SendPump? _pump;
    private readonly string _telemetrySide;
    private int _telemetryConnectionState;
    private const int TelemetryNotOpened = 0;
    private const int TelemetryOpened = 1;
    private const int TelemetryClosed = 2;

    internal void MarkActive()
    {
        var timeProvider = RuntimeContext.TimeProvider;
        Volatile.Write(ref _lastActiveTimestamp, timeProvider.GetTimestamp());
        Volatile.Write(ref _lastActiveUtcTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
    }

    /// <summary>Creates an RPC session that owns one transport connection.</summary>
    /// <param name="connection">The independently owned transport connection.</param>
    /// <param name="creationOptions">The complete immutable session configuration.</param>
    internal RpcSession(ITransportConnection connection, RpcSessionCreationOptions creationOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(creationOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.Id);
        ArgumentNullException.ThrowIfNull(connection.Input);
        ArgumentNullException.ThrowIfNull(connection.Output);

        _transport = connection;
        _lifetimeToken = _cts.Token;
        Id = connection.Id;
        Role = creationOptions.Role;
        RuntimeContext = creationOptions.RuntimeContext;
        _lastActiveTimestamp = RuntimeContext.TimeProvider.GetTimestamp();
        _lastActiveUtcTicks = RuntimeContext.TimeProvider.GetUtcNow().UtcDateTime.Ticks;
        StreamManager = new StreamManager(
            creationOptions.RuntimeContext.Concurrency,
            AcceptReceivedStreamBytes,
            OnStreamBytesConsumed,
            OnReceiveStreamCompleted,
            creationOptions.RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection,
            Fault);
        _flushOptions = creationOptions.FlushOptions;
        _telemetrySide = creationOptions.TelemetrySide;
    }

    internal IRpcByteBufferWriter RentFrameWriter()
        => RuntimeContext.Buffers.Rent(checked(ProtocolV2Constants.HeaderBytes + NegotiatedMaxFramePayloadBytes));

    internal ValueTask AcquireStreamSendCreditAsync(
        long requestId,
        ushort streamId,
        int encodedBytes,
        CancellationToken cancellationToken)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController;
        return controller is null
            ? ValueTask.CompletedTask
            : controller.AcquireSendCreditAsync(requestId, streamId, encodedBytes, cancellationToken);
    }

    internal void ReturnUnsentStreamCredit(long requestId, ushort streamId, int encodedBytes)
        => Volatile.Read(ref _protocolState).FlowController?.ReturnUnsentCredit(requestId, streamId, encodedBytes);

    internal void ApplyWindowUpdate(long requestId, in ProtocolV2WindowUpdate update)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController ??
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.ProtocolState,
                "WindowUpdate was received without negotiated flow control.");
        controller.ApplyWindowUpdate(requestId, update.StreamId, checked((int)update.Credit));
    }

    internal void CompleteSendStream(long requestId, ushort streamId, Exception? exception = null)
        => Volatile.Read(ref _protocolState).FlowController?.CompleteSendStream(requestId, streamId, exception);

    internal void AbortSendStreams(long requestId, Exception exception)
        => Volatile.Read(ref _protocolState).FlowController?.AbortSendStreams(requestId, exception);

    private void AcceptReceivedStreamBytes(long requestId, ushort streamId, int encodedBytes)
        => Volatile.Read(ref _protocolState).FlowController?.AcceptReceived(requestId, streamId, encodedBytes);

    private void OnStreamBytesConsumed(long requestId, ushort streamId, int encodedBytes)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController;
        var credit = controller?.RecordConsumed(requestId, streamId, encodedBytes) ?? 0;
        if (credit != 0)
            TrySendWindowUpdate(requestId, streamId, credit);
        DrainConsumedCreditUpdates(controller);
    }

    private void OnReceiveStreamCompleted(long requestId, ushort streamId)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController;
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

        var result = GetOrCreatePumpOrReturn(packet)
            .TryEnqueue(CreateFrame(packet, forceFlush: false, flushCompletion: null));
        if (result == SendEnqueueResult.Full)
        {
            throw SharpLinkResourceExhaustion.Create(
                SharpLinkResourceExhaustion.SendQueueCapacity,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit (send_queue_capacity).");
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
        var frame = CreateFrame(packet, forceFlush, completion);
        var pump = GetOrCreatePumpOrReturn(packet);
        var result = waitForCapacity
            ? await pump.EnqueueAsync(frame, ct).ConfigureAwait(false)
            : pump.TryEnqueue(frame);
        if (result == SendEnqueueResult.Full)
        {
            throw SharpLinkResourceExhaustion.Create(
                SharpLinkResourceExhaustion.SendQueueCapacity,
                $"Session send queue exceeded its {RuntimeContext.FlowControl.MaxSendQueueBytes}-byte limit (send_queue_capacity).");
        }
        if (result == SendEnqueueResult.Closed)
            throw GetTerminalException();

        if (completion is not null)
        {
            try
            {
                await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The pump still owns the frame and completes this waiter during teardown.
                // Task.WaitAsync does not observe a source fault that arrives after its own
                // cancellation (an already-cancelled token cancels the wait without
                // registering on the source), so observe the late fault here to keep it off
                // the finalizer (issue #216).
                ObserveAbandonedFlushCompletion(completion.Task);
                throw;
            }
        }
    }

    internal ValueTask SendPacketWithBackpressureAsync(
        IRpcByteBufferWriter packet,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (Volatile.Read(ref _terminal) is { } terminal)
            {
                RuntimeContext.Buffers.Return(packet);
                throw terminal.Exception;
            }

            try
            {
                packet = PrepareOutboundPacket(packet, cancellationToken);
            }
            catch
            {
                RuntimeContext.Buffers.Return(packet);
                throw;
            }
            ValidateOutboundPacketOrReturn(packet, allowEmpty: false);

            var frame = CreateFrame(packet, forceFlush: false, flushCompletion: null);
            var pump = GetOrCreatePumpOrReturn(packet);
            var result = pump.TryEnqueueForBackpressure(frame);
            if (result == SendEnqueueResult.Accepted)
                return ValueTask.CompletedTask;
            if (result == SendEnqueueResult.Closed)
                throw GetTerminalException();
            return AwaitBackpressureEnqueueAsync(pump, frame, cancellationToken);
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }

    private async ValueTask AwaitBackpressureEnqueueAsync(
        SendPump pump,
        OwnedFrame frame,
        CancellationToken cancellationToken)
    {
        var result = await pump.EnqueueAsync(frame, cancellationToken).ConfigureAwait(false);
        if (result == SendEnqueueResult.Closed)
            throw GetTerminalException();
    }

    private static OwnedFrame CreateFrame(
        IRpcByteBufferWriter packet,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion)
        => new(
            packet,
            forceFlush,
            flushCompletion,
            IsProtocolProgressFrame(packet.WrittenSpan));

    private static void ObserveAbandonedFlushCompletion(Task flushCompletion)
        => _ = ObserveAbandonedFlushCompletionAsync(flushCompletion);

    private static async Task ObserveAbandonedFlushCompletionAsync(Task flushCompletion)
    {
        try
        {
            await flushCompletion.ConfigureAwait(false);
        }
        catch
        {
            // Observation only: the cancelled enqueuer already surfaced its own cancellation.
        }
    }

    /// <summary>
    /// Classifies protocol progress frames by their header type. Progress
    /// frames carry connection liveness, flow-control credit, and drain state
    /// and must remain timely while bulk stream data saturates the send
    /// queue. RPC data frames, responses, stream-complete frames, and cancels
    /// stay in the normal class: a cancel must never overtake the request it
    /// cancels, because the peer discards cancels for requests it has not
    /// dispatched yet (StreamData before StreamComplete is likewise preserved).
    /// </summary>
    private static bool IsProtocolProgressFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < ProtocolV2Constants.HeaderBytes)
            return false;
        return (ProtocolV2FrameType)frame[5] is
            ProtocolV2FrameType.Ping or
            ProtocolV2FrameType.Pong or
            ProtocolV2FrameType.WindowUpdate or
            ProtocolV2FrameType.GoAway;
    }

    private void ValidateOutboundPacketOrReturn(IRpcByteBufferWriter packet, bool allowEmpty)
    {
        try
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
                throw terminal.Exception;

            var length = packet.WrittenCount;
            if (length == 0)
            {
                if (allowEmpty)
                    return;
                throw new InvalidOperationException("Only an internal flush marker may be empty.");
            }
            if (length < ProtocolV2Constants.HeaderBytes)
                throw new InvalidOperationException("Outbound frame is shorter than the protocol header.");

            var protocolState = Volatile.Read(ref _protocolState);
            if (protocolState.Phase is RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal &&
                Volatile.Read(ref _terminal) is { } phaseTerminal)
            {
                throw phaseTerminal.Exception;
            }
            var maxFramePayloadBytes = protocolState.Options?.MaxFramePayloadBytes ??
                RuntimeContext.Protocol.MaxFramePayloadBytes;
            var payloadLength = length - ProtocolV2Constants.HeaderBytes;
            if (payloadLength > maxFramePayloadBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ResourceExhausted,
                    $"Outbound frame payload exceeds the negotiated {maxFramePayloadBytes}-byte limit.");
            }

            var span = packet.WrittenSpan;
            if (span[0] != ProtocolV2Constants.Magic)
                throw new InvalidOperationException("Outbound frame has an invalid protocol magic byte.");
            EnsureOutboundFrameAllowed(protocolState.Phase, (ProtocolV2FrameType)span[5]);
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

    internal bool IsDraining => ProtocolPhase == RpcSessionProtocolPhase.Draining;

    internal bool CanAcceptCalls =>
        ProtocolPhase == RpcSessionProtocolPhase.Ready && IsConnected;

    /// <summary>
    /// Validates a stable Session lifecycle snapshot at a transition or test boundary.
    /// This intentionally does not run for every frame or request.
    /// </summary>
    internal void AssertStateInvariant()
    {
        var phase = ProtocolPhase;
        var acceptsCalls = CanAcceptCalls;
        if (phase == RpcSessionProtocolPhase.Ready && !acceptsCalls)
        {
            throw new InvalidOperationException(
                "A Ready RPC session must remain connected and accept new calls at a stable lifecycle boundary.");
        }
        if (acceptsCalls && phase is (
                RpcSessionProtocolPhase.Draining or
                RpcSessionProtocolPhase.Stopping or
                RpcSessionProtocolPhase.Terminal))
        {
            throw new InvalidOperationException(
                "A draining or terminal RPC session must not accept a new call at a stable lifecycle boundary.");
        }
        if (phase is RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal)
        {
            if (Volatile.Read(ref _terminal) is null)
            {
                throw new InvalidOperationException(
                    "A stopping or terminal RPC session must publish its terminal reason before the stable lifecycle boundary.");
            }
            if (!StreamManager.IsTerminated)
            {
                throw new InvalidOperationException(
                    "A stopping or terminal RPC session must publish receive-stream termination before the stable lifecycle boundary.");
            }
            if (Volatile.Read(ref _pump) is { } pump && !pump.IsStopRequested)
            {
                throw new InvalidOperationException(
                    "A stopping or terminal RPC session must request send-pump stop before the stable lifecycle boundary.");
            }
        }
    }

    internal void MarkDraining()
        => TransitionProtocolPhase(
            RpcSessionProtocolPhase.Ready,
            RpcSessionProtocolPhase.Draining);

    internal event Action? OnConnected;
    internal void NotifyConnected()
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
    internal event Action<Exception?>? OnDisconnected;
    internal void NotifyDisconnected(Exception? exception = null)
        => Fault(exception ?? new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport closed."));

    private void Fault(Exception exception)
    {
        var structured = exception as SharpLinkException ??
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Transport closed.", exception);
        var terminal = new SessionTerminal(SessionTerminalState.Faulted, structured);
        if (Interlocked.CompareExchange(ref _terminal, terminal, null) is not null)
            return;

        TransitionProtocolPhaseToTerminal();
        RecordTelemetryConnectionClosed();
        CancelSession();
        Volatile.Read(ref _protocolState).FlowController?.Complete(structured);
        Volatile.Read(ref _pump)?.Stop();
        CompleteReceiveStreams(structured);
        ObserveTransportDispose(StartTransportDispose());
        try
        {
            OnDisconnected?.Invoke(structured);
        }
        catch
        {
        }
    }

    internal async ValueTask DisposeAsync()
    {
        BeginShutdown();

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
                CancelSession();
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
                await StartTransportDispose().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupException = CombineCleanupExceptions(cleanupException, exception);
            }
        }
        finally
        {
            TransitionProtocolPhaseToTerminal();
            DisposeSessionCancellation();
            if (cleanupException is null)
                _stoppedTcs.TrySetResult(true);
            else
                _stoppedTcs.TrySetException(cleanupException);
        }

        if (cleanupException is not null)
        {
            // A single-owner dispose has no other observer for the faulted TCS task: the
            // rethrow below surfaces only a copy to the caller, so observe the task here
            // to keep the fault off the finalizer (issue #216).
            _ = _stoppedTcs.Task.Exception;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }
    }

    internal void BeginShutdown()
    {
        var stopping = new SessionTerminal(
            SessionTerminalState.Stopping,
            new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Session is stopping."));
        var existing = Interlocked.CompareExchange(ref _terminal, stopping, null);
        var terminal = existing ?? stopping;
        if (existing is null)
            RecordTelemetryConnectionClosed();

        TransitionProtocolPhaseToStopping();

        // These registrations can race the terminal transition during handshake. Repeat the
        // idempotent signal on every caller so a late publication cannot keep shutdown joined.
        Volatile.Read(ref _protocolState).FlowController?.Complete(terminal.Exception);
        CompleteReceiveStreams(terminal.Exception);
        if (existing is null)
        {
            try
            {
                OnDisconnected?.Invoke(terminal.Exception);
            }
            catch
            {
            }
        }
        Volatile.Read(ref _pump)?.Stop();
    }

    private void CancelSession()
    {
        lock (_ctsGate)
        {
            if (_ctsCancellationSignaled)
                return;

            // Publish ownership before callbacks run so cancellation cannot re-enter this path.
            _ctsCancellationSignaled = true;
            _cts.Cancel();
        }
    }

    private void DisposeSessionCancellation()
    {
        lock (_ctsGate)
        {
            _cts.Dispose();
            Volatile.Write(ref _stopped, 1);
        }
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
                RuntimeContext.PerformanceProfile,
                RuntimeContext.FlowControl.MaxSendQueueBytes,
                _flushOptions,
                RuntimeContext.TimeProvider,
                _cts.Token,
                ReturnBuffer,
                Fault);
            Volatile.Write(ref _pump, pump);
            return pump;
        }
    }

    private SendPump GetOrCreatePumpOrReturn(IRpcByteBufferWriter packet)
    {
        try
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
                throw terminal.Exception;
            return GetOrCreatePump();
        }
        catch
        {
            RuntimeContext.Buffers.Return(packet);
            throw;
        }
    }

    private Task StartTransportDispose()
    {
        lock (_transportDisposeGate)
        {
            if (_transportDisposeTask is not null)
                return _transportDisposeTask;

            try
            {
                _transportDisposeTask = _transport.DisposeAsync().AsTask();
            }
            catch (Exception ex)
            {
                _transportDisposeTask = Task.FromException(ex);
            }

            return _transportDisposeTask;
        }
    }

    private static void ObserveTransportDispose(Task disposeTask)
    {
        if (!disposeTask.IsCompletedSuccessfully)
            _ = ObserveTransportDisposeAsync(disposeTask);
    }

    private static async Task ObserveTransportDisposeAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch
        {
            // DisposeAsync awaits the same single-flight task and preserves this failure for the owner.
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
