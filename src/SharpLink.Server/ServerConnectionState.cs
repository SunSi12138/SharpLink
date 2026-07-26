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
    private readonly TaskCompletionSource _sessionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<ServiceRegistration, ConnectionServiceEntry> _services = [];
    private SharpLinkAuthenticationContext? _authenticationContext;
    private SharpLinkCallContextSnapshot? _defaultCallContext;
    private long _lastAcceptedRequestId;
    private int _activeCalls;
    private int _lifecycleState = (int)ServerConnectionLifecycleState.Handshaking;
    private int _closeStarted;
    private Task? _serviceCleanupTask;

    internal ServerConnectionState(
        RpcSession session,
        RuntimeConcurrencyOptions concurrency,
        CancellationToken serverToken,
        int maxConcurrentCalls = 1024)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(concurrency);
        CallCancellations = new StripedLongMap<ServerCallCancellationState>(concurrency);
        DeadlineScheduler = new ServerCallDeadlineScheduler(CallCancellations, maxConcurrentCalls);
        _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _connectionToken = _connectionCancellation.Token;
    }

    internal RpcSession Session { get; }

    internal SharpLinkAuthenticationContext? AuthenticationContext
        => Volatile.Read(ref _authenticationContext);

    internal SharpLinkCallContextSnapshot? DefaultCallContext
        => Volatile.Read(ref _defaultCallContext);

    internal SharpLinkCallContextSnapshot GetCallContextSnapshot(
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata)
    {
        if (deadline is null && metadata is null &&
            Volatile.Read(ref _defaultCallContext) is { } defaultCallContext)
        {
            return defaultCallContext;
        }

        return new SharpLinkCallContextSnapshot(
            Session.Id,
            Volatile.Read(ref _authenticationContext),
            deadline,
            metadata);
    }

    internal StripedLongMap<ServerCallCancellationState> CallCancellations { get; }

    internal ServerCallDeadlineScheduler DeadlineScheduler { get; }

    internal CancellationToken ConnectionToken => _connectionToken;

    internal Task SessionTask => _sessionCompleted.Task;

    internal Task ServiceCleanupTask => Volatile.Read(ref _serviceCleanupTask) ?? Task.CompletedTask;

    internal int ActiveCalls => Volatile.Read(ref _activeCalls);

    internal long LastAcceptedRequestId => Volatile.Read(ref _lastAcceptedRequestId);

    internal ServerConnectionLifecycleState LifecycleState
        => (ServerConnectionLifecycleState)Volatile.Read(ref _lifecycleState);

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

    internal ValueTask<ServiceLease> AcquireServiceAsync(
        ServiceRegistration registration,
        SharpLinkDynamicModuleLease moduleLease)
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

        var candidate = new ConnectionServiceEntry(registration);
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
        var entries = new KeyValuePair<long, ServerCallCancellationState>[maximumCalls];
        var count = CallCancellations.CopyEntries(entries);
        var calls = new List<ServerCallDiagnosticSnapshot>(count);
        for (var index = 0; index < count; index++)
        {
            var entry = entries[index];
            if (!entry.Value.TryAcquire(entry.Key))
                continue;
            try
            {
                calls.Add(new ServerCallDiagnosticSnapshot(
                    entry.Key,
                    entry.Value.Reason.ToString(),
                    entry.Value.Deadline,
                    entry.Value.DeadlineTimestamp));
            }
            finally
            {
                entry.Value.ReleaseUse();
            }
        }

        return new ServerConnectionDiagnosticSnapshot(
            Session.Id,
            LifecycleState.ToString(),
            ActiveCalls,
            Session.StreamManager is StreamManager manager ? manager.ActiveStreamCount : -1,
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
                if (ActiveCalls == 0)
                    _callsDrained.TrySetResult();
                return;
            }
        }
    }

    internal ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            return new ValueTask(_sessionCompleted.Task);

        return new ValueTask(CloseCoreAsync());
    }

    private async Task CloseCoreAsync()
    {
        MarkDraining();
        Exception? firstException = null;
        try
        {
            _connectionCancellation.Cancel();
        }
        catch (Exception exception)
        {
            firstException = exception;
        }

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstException ??= exception;
        }
        finally
        {
            if (ActiveCalls == 0)
                _callsDrained.TrySetResult();
            Interlocked.Exchange(ref _lifecycleState, (int)ServerConnectionLifecycleState.Closed);
            Volatile.Write(ref _authenticationContext, null);
            Volatile.Write(ref _defaultCallContext, null);
            _serviceCleanupTask = CleanupServicesWhenCallsDrainAsync();
            if (firstException is null)
                _sessionCompleted.TrySetResult();
            else
                _sessionCompleted.TrySetException(firstException);
        }

        if (firstException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
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

        internal ConnectionServiceEntry(ServiceRegistration registration)
            => _instance = new Lazy<Task<ConnectionServiceInstance>>(
                () => registration.CreateConnectionServiceAsync().AsTask(),
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
