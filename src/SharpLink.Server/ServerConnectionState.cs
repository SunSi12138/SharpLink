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
    private readonly ConcurrentDictionary<ServiceRegistration, Lazy<Task<ConnectionServiceInstance>>> _services = [];
    private SharpLinkAuthenticationContext? _authenticationContext;
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

    internal StripedLongMap<ServerCallCancellationState> CallCancellations { get; }

    internal ServerCallDeadlineScheduler DeadlineScheduler { get; }

    internal CancellationToken ConnectionToken => _connectionToken;

    internal Task SessionTask => _sessionCompleted.Task;

    internal int ActiveCalls => Volatile.Read(ref _activeCalls);

    internal long LastAcceptedRequestId => Volatile.Read(ref _lastAcceptedRequestId);

    internal ServerConnectionLifecycleState LifecycleState
        => (ServerConnectionLifecycleState)Volatile.Read(ref _lifecycleState);

    internal bool MarkReady(SharpLinkAuthenticationContext? authenticationContext)
    {
        Volatile.Write(ref _authenticationContext, authenticationContext);
        if (Interlocked.CompareExchange(
                ref _lifecycleState,
                (int)ServerConnectionLifecycleState.Ready,
                (int)ServerConnectionLifecycleState.Handshaking) ==
            (int)ServerConnectionLifecycleState.Handshaking)
        {
            return true;
        }

        Volatile.Write(ref _authenticationContext, null);
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
            return AwaitConnectionServiceAsync(existing.Value, moduleLease);

        var candidate = new Lazy<Task<ConnectionServiceInstance>>(
            () => registration.CreateConnectionServiceAsync().AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var selected = _services.GetOrAdd(registration, candidate);
        return AwaitConnectionServiceAsync(selected.Value, moduleLease);
    }

    internal async ValueTask DisposeServiceAsync(ServiceRegistration registration)
    {
        if (!_services.TryRemove(registration, out var service) || !service.IsValueCreated)
            return;
        var instance = await service.Value.ConfigureAwait(false);
        await instance.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask<ServiceLease> AwaitConnectionServiceAsync(
        Task<ConnectionServiceInstance> serviceTask,
        SharpLinkDynamicModuleLease moduleLease)
    {
        try
        {
            var service = await serviceTask.ConfigureAwait(false);
            return new ServiceLease(service.Service, moduleLease: moduleLease);
        }
        catch
        {
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
        foreach (var service in _services.Values)
        {
            if (!service.IsValueCreated)
                continue;
            try
            {
                var instance = await service.Value.ConfigureAwait(false);
                await instance.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Connection teardown has already completed. Service cleanup is
                // best-effort here and must never turn a bounded stop into a wait
                // for uncooperative user code.
            }
        }
        _services.Clear();
        DeadlineScheduler.Dispose();
        _connectionCancellation.Dispose();
    }
}
