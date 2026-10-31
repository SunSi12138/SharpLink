namespace SharpLink.Server;

internal enum ServerConnectionLifecycleState : byte
{
    Handshaking,
    Ready,
    Draining,
    Closed
}

/// <summary>Owns all mutable server state associated with one physical RPC connection.</summary>
internal sealed class ServerConnectionState
{
    private readonly CancellationTokenSource _connectionCancellation;
    private readonly CancellationToken _connectionToken;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _sessionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sessionLoopCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<ServiceRegistration, ConnectionServiceEntry> _services = [];
    private readonly Lock _bindingsGate = new();
    private ServerBindingTable? _bindings;
    private bool _bindingsClosed;
    private int _bindingOperations;
    private int _bindingControlFrames;
    private SharpLinkAuthenticationContext? _authenticationContext;
    private SharpLinkCallContextSnapshot? _defaultCallContext;
    private long _lastAcceptedRequestId;
    private int _activeCalls;
    private int _lifecycleState = (int)ServerConnectionLifecycleState.Handshaking;
    private int _sessionLoopState;
    private int _closeStarted;
    private Task? _serviceCleanupTask;
#if DEBUG
    private readonly Action? _afterLocalCallAdmission;
#endif

    internal ServerConnectionState(
        RpcSession session,
        IRpcGeneratedServerBridge generatedBridge,
        StripedLongMap<ServerCallCancellationState> callCancellations,
        CancellationToken serverToken,
        TimeProvider timeProvider,
        int maxConcurrentCalls = 1024
#if DEBUG
        , Action? afterLocalCallAdmission = null
#endif
        )
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        GeneratedBridge = generatedBridge ?? throw new ArgumentNullException(nameof(generatedBridge));
        CallCancellations = callCancellations ?? throw new ArgumentNullException(nameof(callCancellations));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        DeadlineScheduler = new ServerCallDeadlineScheduler(
            CallCancellations,
            maxConcurrentCalls,
            _timeProvider);
        _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _connectionToken = _connectionCancellation.Token;
        Session.OnDisconnected += OnBindingSessionDisconnected;
#if DEBUG
        _afterLocalCallAdmission = afterLocalCallAdmission;
#endif
    }

    internal RpcSession Session { get; }

    internal ServerBindingTable Bindings => Volatile.Read(ref _bindings)
        ?? throw new InvalidOperationException("Session binding state has not been initialized.");

    internal ServerBindingTable? BindingTableOrNull => Volatile.Read(ref _bindings);

    internal ServerBindingTable InitializeBindings(ServerBindingSlotQuota quota)
    {
        lock (_bindingsGate)
        {
            if (_bindings is { } existing)
                return existing;
            var options = Session.NegotiatedOptions
                ?? throw new InvalidOperationException("Binding state requires a completed handshake.");
            if (!Protocol3BindingLimitsCodec.IsValid(options.Authority.Limits) ||
                LifecycleState is ServerConnectionLifecycleState.Handshaking or ServerConnectionLifecycleState.Closed)
                throw new InvalidOperationException("Binding state requires an authenticated negotiated Session.");
            var table = new ServerBindingTable(options.Authority.Limits.Slots, quota);
            if (_bindingsClosed || Volatile.Read(ref _closeStarted) != 0 || !Session.IsConnected)
                table.Close();
            Volatile.Write(ref _bindings, table);
            return table;
        }
    }

    internal bool TryReserveBindingOperation(ushort limit) => TryReserveBindingCount(ref _bindingOperations, limit);
    internal bool TryReserveBindingControlFrame(ushort limit) => TryReserveBindingCount(ref _bindingControlFrames, limit);
    internal void ReleaseBindingOperation() => ReleaseBindingCount(ref _bindingOperations);
    internal void ReleaseBindingControlFrame() => ReleaseBindingCount(ref _bindingControlFrames);

    internal void CloseBindings()
    {
        lock (_bindingsGate)
        {
            _bindingsClosed = true;
            _bindings?.Close();
        }
    }

    internal void FailBindingOperation(Exception exception)
    {
        CloseBindings();
        Session.NotifyDisconnected(exception);
    }

    private void OnBindingSessionDisconnected(Exception? exception) => CloseBindings();

    private static bool TryReserveBindingCount(ref int count, ushort limit)
    {
        if (limit == 0)
            return false;
        while (true)
        {
            var observed = Volatile.Read(ref count);
            if (observed >= limit)
                return false;
            if (Interlocked.CompareExchange(ref count, observed + 1, observed) == observed)
                return true;
        }
    }

    private static void ReleaseBindingCount(ref int count)
    {
        if (Interlocked.Decrement(ref count) < 0)
            throw new InvalidOperationException("Binding control ownership was released more than once.");
    }

    internal IRpcGeneratedServerBridge GeneratedBridge { get; }

    internal SharpLinkAuthenticationContext? AuthenticationContext
        => Volatile.Read(ref _authenticationContext);

    internal SharpLinkCallContextSnapshot? DefaultCallContext
        => Volatile.Read(ref _defaultCallContext);

    internal SharpLinkCallContextSnapshot GetCallContextSnapshot(
        RpcDeadline deadline,
        SharpLinkMetadata? metadata)
    {
        if (!deadline.HasValue && metadata is null &&
            Volatile.Read(ref _defaultCallContext) is { } defaultCallContext)
        {
            return defaultCallContext;
        }

        return new SharpLinkCallContextSnapshot(
            Session.Id,
            Volatile.Read(ref _authenticationContext),
            deadline,
            _timeProvider,
            metadata);
    }

    internal StripedLongMap<ServerCallCancellationState> CallCancellations { get; }

    internal ServerCallDeadlineScheduler DeadlineScheduler { get; }

    internal CancellationToken ConnectionToken => _connectionToken;

    internal Task SessionTask => _sessionCompleted.Task;

    internal Task ServiceCleanupTask => Volatile.Read(ref _serviceCleanupTask) ?? Task.CompletedTask;

    internal int ActiveCalls => Volatile.Read(ref _activeCalls);

    /// <summary>
    /// Validates a stable server-connection lifecycle snapshot at a transition or test boundary.
    /// This intentionally stays outside the request/frame hot path and takes no locks.
    /// </summary>
    internal void AssertStateInvariant()
    {
        if (ActiveCalls < 0)
            throw new InvalidOperationException("Server connection active call count became negative.");

        var state = LifecycleState;
        var sessionAcceptsCalls = Session.CanAcceptCalls;
        if (state == ServerConnectionLifecycleState.Ready &&
            (!sessionAcceptsCalls || Session.NegotiatedOptions is null || DefaultCallContext is null))
        {
            throw new InvalidOperationException(
                "A Ready server connection must have a negotiated, call-accepting Session and a published authentication/call-context snapshot at a stable lifecycle boundary.");
        }
        if (sessionAcceptsCalls && state is (
                ServerConnectionLifecycleState.Draining or
                ServerConnectionLifecycleState.Closed))
        {
            throw new InvalidOperationException(
                "A draining or closed server connection must not reference a Session that accepts new calls at a stable lifecycle boundary.");
        }
    }

    internal long LastAcceptedRequestId => Volatile.Read(ref _lastAcceptedRequestId);

    internal ServerConnectionLifecycleState LifecycleState
        => (ServerConnectionLifecycleState)Volatile.Read(ref _lifecycleState);

    internal void MarkSessionLoopStarted()
    {
        if (Interlocked.CompareExchange(ref _sessionLoopState, 1, 0) != 0)
            throw new InvalidOperationException("Server connection session loop was already started.");
    }

    internal void MarkSessionLoopCompleted()
    {
        if (Interlocked.Exchange(ref _sessionLoopState, 2) == 1)
            _sessionLoopCompleted.TrySetResult();
    }

    internal bool MarkReady(SharpLinkAuthenticationContext? authenticationContext)
    {
        var defaultCallContext = new SharpLinkCallContextSnapshot(Session.Id, authenticationContext);
        Volatile.Write(ref _authenticationContext, authenticationContext);
        Volatile.Write(ref _defaultCallContext, defaultCallContext);
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                (int)ServerConnectionLifecycleState.Ready,
                (int)ServerConnectionLifecycleState.Handshaking) ==
            (int)ServerConnectionLifecycleState.Handshaking)
        {
            return true;
        }

        Volatile.Write(ref _authenticationContext, null);
        Volatile.Write(ref _defaultCallContext, null);
        return false;
    }

    internal bool TryRecordAcceptedRequest(long requestId)
    {
        if (LifecycleState != ServerConnectionLifecycleState.Ready)
            return false;

        Volatile.Write(ref _lastAcceptedRequestId, requestId);
        return LifecycleState == ServerConnectionLifecycleState.Ready;
    }

    internal bool TryAcquireCall(int maxConcurrentCalls)
    {
        if (LifecycleState != ServerConnectionLifecycleState.Ready)
            return false;

        if (Interlocked.Increment(ref _activeCalls) > maxConcurrentCalls)
        {
            Interlocked.Decrement(ref _activeCalls);
            return false;
        }

        if (LifecycleState == ServerConnectionLifecycleState.Ready)
            return true;

        Interlocked.Decrement(ref _activeCalls);
        return false;
    }

    internal void ReleaseCall()
    {
        var remaining = Interlocked.Decrement(ref _activeCalls);
        if (remaining < 0)
            throw new InvalidOperationException("Server connection active call count underflowed.");
        if (remaining == 0 && LifecycleState >= ServerConnectionLifecycleState.Draining)
            _callsDrained.TrySetResult();
    }

#if DEBUG
    /// <summary>
    /// Deterministic UnitTests-only seam for the exact local-to-global admission
    /// transfer. Release builds omit both this method and the nullable observer.
    /// </summary>
    internal void NotifyAfterLocalCallAdmissionForTesting()
        => _afterLocalCallAdmission?.Invoke();
#endif

    internal ValueTask<ServiceLease> AcquireServiceAsync(
        ServiceRegistration registration,
        SharpLinkDynamicModuleLease moduleLease)
        => AcquireServiceAsync(
            registration,
            moduleLease,
            generatedBridge: null,
            requestId: 0);

    internal ValueTask<ServiceLease> AcquireServiceAsync(
        ServiceRegistration registration,
        SharpLinkDynamicModuleLease moduleLease,
        IRpcGeneratedServerBridge? generatedBridge,
        long requestId)
    {
        if (LifecycleState != ServerConnectionLifecycleState.Ready)
        {
            moduleLease.Dispose();
            return ValueTask.FromException<ServiceLease>(new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "RPC connection is draining."));
        }
        if (_services.TryGetValue(registration, out var existing))
            return AwaitConnectionServiceAsync(registration, existing, moduleLease);

        var candidate = new ConnectionServiceEntry(registration, generatedBridge, requestId);
        var selected = _services.GetOrAdd(registration, candidate);
        return AwaitConnectionServiceAsync(registration, selected, moduleLease);
    }

    internal async ValueTask DisposeServiceAsync(ServiceRegistration registration)
    {
        if (!_services.TryGetValue(registration, out var service))
            return;
        try
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _services.TryRemove(
                new KeyValuePair<ServiceRegistration, ConnectionServiceEntry>(registration, service));
        }
    }

    private async ValueTask<ServiceLease> AwaitConnectionServiceAsync(
        ServiceRegistration registration,
        ConnectionServiceEntry entry,
        SharpLinkDynamicModuleLease moduleLease)
    {
        try
        {
            var service = await entry.GetServiceAsync().ConfigureAwait(false);
            return new ServiceLease(service.Service, moduleLease: moduleLease);
        }
        catch
        {
            _services.TryRemove(
                new KeyValuePair<ServiceRegistration, ConnectionServiceEntry>(registration, entry));
            moduleLease.Dispose();
            throw;
        }
    }

    internal ServerConnectionDiagnosticSnapshot CaptureStopDiagnostics(int maximumCalls)
    {
        var entries = new ServerCallCancellationLease[maximumCalls];
        var count = CallCancellations.CopyEntries(
            entries,
            static (requestId, state) => state.CaptureLease(requestId));
        var calls = new List<ServerCallDiagnosticSnapshot>(count);
        for (var index = 0; index < count; index++)
        {
            var callLease = entries[index];
            if (!callLease.TryAcquire())
                continue;
            try
            {
                var call = callLease.State;
                calls.Add(new ServerCallDiagnosticSnapshot(
                    callLease.RequestId,
                    call.Reason.ToString(),
                    call.Deadline.Timestamp));
            }
            finally
            {
                callLease.ReleaseUse();
            }
        }

        return new ServerConnectionDiagnosticSnapshot(
            Session.Id,
            LifecycleState.ToString(),
            ActiveCalls,
            Session.StreamManager.ActiveStreamCount,
            calls);
    }

    internal void MarkDraining()
    {
        while (true)
        {
            var current = Volatile.Read(ref _lifecycleState);
            if (current >= (int)ServerConnectionLifecycleState.Draining)
                return;
            if (Interlocked.CompareExchange(
                    ref _lifecycleState,
                    (int)ServerConnectionLifecycleState.Draining,
                    current) == current)
            {
                Session.MarkDraining();
                if (ActiveCalls == 0)
                    _callsDrained.TrySetResult();
                return;
            }
        }
    }

    internal ValueTask CloseAsync()
    {
        CloseBindings();
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            return new ValueTask(_sessionCompleted.Task);

        return new ValueTask(CloseCoreAsync());
    }

    private async Task CloseCoreAsync()
    {
        MarkDraining();
        List<Exception>? failures = null;
        try
        {
            _connectionCancellation.Cancel();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        // Stop stream dispatch and the send pump before joining the read loop. The loop can
        // itself be awaiting bounded stream delivery; completing those dispatchers releases
        // it without completing or reclaiming its active PipeReader buffer.
        Session.BeginShutdown();

        try
        {
            // Cancellation is normally sufficient, but CancelPendingRead also wakes custom
            // PipeReader implementations that do not complete a pending read from the token.
            // It does not release an already returned ReadResult; the session loop still owns
            // that buffer until it calls AdvanceTo and publishes completion below.
            Session.Input.CancelPendingRead();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (Volatile.Read(ref _sessionLoopState) == 1)
            await _sessionLoopCompleted.Task.ConfigureAwait(false);

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            if (ActiveCalls == 0)
                _callsDrained.TrySetResult();
            Interlocked.Exchange(ref _lifecycleState, (int)ServerConnectionLifecycleState.Closed);
            Volatile.Write(ref _authenticationContext, null);
            Volatile.Write(ref _defaultCallContext, null);
            _serviceCleanupTask = CleanupServicesWhenCallsDrainAsync();
            if (failures is null)
                _sessionCompleted.TrySetResult();
            else if (failures.Count == 1)
                _sessionCompleted.TrySetException(failures[0]);
            else
                _sessionCompleted.TrySetException(new AggregateException(failures));
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private async Task CleanupServicesWhenCallsDrainAsync()
    {
        await _callsDrained.Task.ConfigureAwait(false);
        List<Exception>? failures = null;
        foreach (var registration in _services.Keys)
        {
            try
            {
                await DisposeServiceAsync(registration).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        _services.Clear();
        try
        {
            DeadlineScheduler.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        try
        {
            _connectionCancellation.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private sealed class ConnectionServiceEntry
    {
        private readonly Lazy<Task<ConnectionServiceInstance>> _instance;
        private readonly Lock _disposeGate = new();
        private Task? _disposeTask;

        internal ConnectionServiceEntry(
            ServiceRegistration registration,
            IRpcGeneratedServerBridge? generatedBridge,
            long requestId)
            => _instance = new Lazy<Task<ConnectionServiceInstance>>(
                () => registration.CreateConnectionServiceAsync(generatedBridge, requestId).AsTask(),
                LazyThreadSafetyMode.ExecutionAndPublication);

        internal Task<ConnectionServiceInstance> GetServiceAsync() => _instance.Value;

        internal Task DisposeAsync()
        {
            lock (_disposeGate)
                return _disposeTask ??= DisposeCoreAsync();
        }

        private async Task DisposeCoreAsync()
        {
            if (!_instance.IsValueCreated)
                return;
            ConnectionServiceInstance instance;
            try
            {
                instance = await _instance.Value.ConfigureAwait(false);
            }
            catch
            {
                // Failed activation created no service instance. Acquisition evicts
                // the entry so cleanup must not replay the constructor exception.
                return;
            }
            await instance.DisposeAsync().ConfigureAwait(false);
        }
    }
}
