using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    IServerTransportListener transportListener,
    FrozenDictionary<long, ServiceRegistration> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory,
    ISharpLinkServerAuthenticator? authenticator = null,
    bool authenticationRequired = false,
    SharpLinkProtocolOptions? protocolOptions = null,
    SharpLinkRuntimeContext? runtimeContext = null,
    RpcSessionFlushOptions? rpcSessionFlushOptions = null,
    ISharpLinkServerInterceptor[]? serverInterceptors = null,
    IRpcExceptionMapper? exceptionMapper = null,
    IAsyncDisposable? ownedServiceProvider = null) : ISharpLinkServer
{
    private enum ServerState
    {
        Created,
        Starting,
        Running,
        Draining,
        Stopped,
        Faulted
    }

    private enum ServerCallAdmissionResult : byte
    {
        Acquired,
        Unavailable,
        CapacityExhausted
    }

    private readonly SharpLinkRuntimeContext _runtimeContext = runtimeContext ?? new SharpLinkRuntimeContextBuilder().Build();
    private readonly ConcurrentDictionary<string, ServerConnectionState> _connections = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();
    private readonly ISharpLinkServerAuthenticator? _authenticator = authenticator;
    private readonly bool _authenticationRequired = authenticationRequired;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _frameworkTasksGate = new();
    private readonly HashSet<Task> _frameworkTasks = [];
    private readonly TaskCompletionSource<bool> _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;
    private Task? _stopTask;
    private int _state = (int)ServerState.Created;
    private readonly SharpLinkProtocolOptions _protocolOptions =
        (protocolOptions ?? runtimeContext?.Protocol ?? new SharpLinkProtocolOptions()).CloneValidated();
    private readonly int _maxConcurrentCallsPerConnection =
        (runtimeContext?.FlowControl.MaxConcurrentCallsPerConnection ?? 1024);
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions = rpcSessionFlushOptions;
    private readonly ISharpLinkServerInterceptor[] _serverInterceptors =
        serverInterceptors is { Length: > 0 } ? [.. serverInterceptors] : [];
    private readonly IRpcExceptionMapper _exceptionMapper =
        exceptionMapper ?? new DefaultRpcExceptionMapper(includeDetails: false);
    private readonly ServerServiceCleanup _serviceCleanup = new(services.Values, ownedServiceProvider);
    private Task? _deferredServiceCleanupTask;
    private Task? _shutdownCleanupObserver;
    private Task? _serviceCleanupObserver;
    private ServerStopDiagnosticSnapshot? _lastStopDiagnostics;
    private int _globalActiveCalls;
    private long _rejectedOneWayCalls;
    private readonly int _globalMaxConcurrentCalls = (int)Math.Min(
        (long)Environment.ProcessorCount * 1024,
        65_536L);

    public SharpLinkHealthStatus HealthStatus => CurrentState switch
    {
        ServerState.Running => SharpLinkHealthStatus.Ready,
        ServerState.Draining => SharpLinkHealthStatus.Draining,
        _ => SharpLinkHealthStatus.Unhealthy
    };

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.Zero);

    public ValueTask StopAsync(
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        Task stopTask;
        lock (_stateGate)
        {
            _stopTask ??= StopCoreAsync(gracefulTimeout);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(stopTask.WaitAsync(cancellationToken))
            : new ValueTask(stopTask);
    }

    private async Task StopCoreAsync(TimeSpan gracefulTimeout)
    {
        const int cleanupBudgetSeconds = 5;
        var started = Stopwatch.GetTimestamp();
        var gracefulDeadline = AddStopwatchDuration(started, gracefulTimeout);
        var finalDeadline = AddStopwatchDuration(gracefulDeadline, TimeSpan.FromSeconds(cleanupBudgetSeconds));
        var faulted = false;

        TransitionTo(ServerState.Draining);
        CancelForShutdown(_acceptCts, _logger, "AcceptCancellation");
        var listenerDisposeTask = StartListenerDispose(transportListener);
        var goAwayTask = SendGoAwayToAllAsync();

        try
        {
            if (Volatile.Read(ref _globalActiveCalls) == 0)
                _callsDrained.TrySetResult(true);
            else
                await WaitUntilAsync(_callsDrained.Task, gracefulDeadline).ConfigureAwait(false);

            Task flushTask = Task.CompletedTask;
            if (_callsDrained.Task.IsCompletedSuccessfully)
                flushTask = FlushAllSessionsAsync();

            var unfinishedCalls = Volatile.Read(ref _globalActiveCalls);
            if (unfinishedCalls > 0)
            {
                Volatile.Write(ref _lastStopDiagnostics, CaptureStopDiagnostics(unfinishedCalls));
                LogForcedCallsRemaining(_logger, unfinishedCalls);
                SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
                _deferredServiceCleanupTask = DisposeServicesWhenDrainedAsync(
                    _callsDrained.Task,
                    _serviceCleanup,
                    _logger);
            }

            CancelForShutdown(_forceStopCts, _logger, "CallCancellation");
            var closeSessionsTask = DisposeAllSessionsAsync();
            var frameworkTasksTask = WaitForFrameworkTasksAsync();
            var frameworkCleanupTask = Task.WhenAll(
                listenerDisposeTask,
                goAwayTask,
                flushTask,
                closeSessionsTask,
                frameworkTasksTask);

            var frameworkCleanupCompleted = false;
            try
            {
                frameworkCleanupCompleted = await WaitUntilAsync(
                    frameworkCleanupTask,
                    finalDeadline).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                faulted = true;
                frameworkCleanupCompleted = true;
                LogDeferredCleanupFailed(_logger, "Framework", exception);
            }

            if (!frameworkCleanupCompleted)
            {
                faulted = true;
                LogFrameworkCleanupTimeout(_logger, cleanupBudgetSeconds);
                _shutdownCleanupObserver = ObserveShutdownAndDisposeTokensAsync(
                    frameworkCleanupTask,
                    _acceptCts,
                    _forceStopCts,
                    _logger);
            }
            else
            {
                _acceptCts.Dispose();
                _forceStopCts.Dispose();
            }

            if (unfinishedCalls == 0)
            {
                var serviceCleanupTask = _serviceCleanup.DisposeAsync().AsTask();
                if (!await WaitUntilAsync(serviceCleanupTask, finalDeadline).ConfigureAwait(false))
                {
                    faulted = true;
                    _serviceCleanupObserver = ObserveCleanupFailureAsync(
                        serviceCleanupTask,
                        _logger,
                        "Services");
                }
            }
        }
        catch (Exception exception)
        {
            faulted = true;
            LogDeferredCleanupFailed(_logger, "Stop", exception);
        }

        TransitionTo(faulted ? ServerState.Faulted : ServerState.Stopped);
    }

    private async Task CleanupAfterRunFailureAsync()
    {
        const int cleanupBudgetSeconds = 5;
        var deadline = AddStopwatchDuration(
            Stopwatch.GetTimestamp(),
            TimeSpan.FromSeconds(cleanupBudgetSeconds));

        CancelForShutdown(_acceptCts, _logger, "AcceptCancellation");
        CancelForShutdown(_forceStopCts, _logger, "CallCancellation");
        if (Volatile.Read(ref _globalActiveCalls) == 0)
            _callsDrained.TrySetResult(true);
        else
        {
            var unfinishedCalls = Volatile.Read(ref _globalActiveCalls);
            LogForcedCallsRemaining(_logger, unfinishedCalls);
            SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
            _deferredServiceCleanupTask ??= DisposeServicesWhenDrainedAsync(
                _callsDrained.Task,
                _serviceCleanup,
                _logger);
        }

        var frameworkCleanupTask = Task.WhenAll(
            StartListenerDispose(transportListener),
            DisposeAllSessionsAsync(),
            WaitForFrameworkTasksAsync());
        var frameworkCleanupCompleted = false;
        try
        {
            frameworkCleanupCompleted = await WaitUntilAsync(frameworkCleanupTask, deadline)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            frameworkCleanupCompleted = true;
            LogDeferredCleanupFailed(_logger, "Framework", exception);
        }

        if (frameworkCleanupCompleted)
        {
            _acceptCts.Dispose();
            _forceStopCts.Dispose();
        }
        else
        {
            LogFrameworkCleanupTimeout(_logger, cleanupBudgetSeconds);
            _shutdownCleanupObserver = ObserveShutdownAndDisposeTokensAsync(
                frameworkCleanupTask,
                _acceptCts,
                _forceStopCts,
                _logger);
        }

        if (_callsDrained.Task.IsCompletedSuccessfully)
        {
            var serviceCleanupTask = _serviceCleanup.DisposeAsync().AsTask();
            try
            {
                if (!await WaitUntilAsync(serviceCleanupTask, deadline).ConfigureAwait(false))
                {
                    _serviceCleanupObserver = ObserveCleanupFailureAsync(
                        serviceCleanupTask,
                        _logger,
                        "Services");
                }
            }
            catch (Exception exception)
            {
                LogDeferredCleanupFailed(_logger, "Services", exception);
            }
        }
    }

    private async Task SendGoAwayToAllAsync()
    {
        var connections = _connections.Values.ToArray();
        var tasks = new Task[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            connection.MarkDraining();
            tasks[index] = SendGoAwayAsync(connection);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static Task StartListenerDispose(IServerTransportListener listener)
    {
        try
        {
            return listener.DisposeAsync().AsTask();
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private static void CancelForShutdown(
        CancellationTokenSource cancellation,
        ILogger logger,
        string cleanupName)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(logger, cleanupName, exception);
        }
    }

    private static async Task SendGoAwayAsync(ServerConnectionState connection)
    {
        try
        {
            await connection.Session.SendGoAwayAsync(
                connection.LastAcceptedRequestId,
                SharpLinkErrorCode.Unavailable,
                "Server is draining.").ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SharpLinkException or System.IO.IOException or ObjectDisposedException)
        {
        }
    }

    private async Task FlushAllSessionsAsync()
    {
        var connections = _connections.Values.ToArray();
        var tasks = new Task[connections.Length];
        for (var index = 0; index < connections.Length; index++)
            tasks[index] = FlushSessionAsync(connections[index]);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task FlushSessionAsync(ServerConnectionState connection)
    {
        try
        {
            await connection.Session.FlushSendQueueAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SharpLinkException or System.IO.IOException or ObjectDisposedException)
        {
        }
    }

    private void TrackFrameworkTask(Task task)
    {
        lock (_frameworkTasksGate)
            _frameworkTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var server = (SharpLinkServer)state!;
                lock (server._frameworkTasksGate)
                    server._frameworkTasks.Remove(completedTask);

                if (completedTask.Exception is { } exception)
                {
                    LogServerBackgroundLoopUnhandledException(
                        server._logger,
                        "FrameworkTask",
                        exception.GetBaseException());
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeAllSessionsAsync()
    {
        var connections = _connections.Values.ToArray();
        _connections.Clear();
        var tasks = new Task[connections.Length];
        for (var index = 0; index < connections.Length; index++)
            tasks[index] = connections[index].CloseAsync().AsTask();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException or
                System.IO.IOException or SocketException or SharpLinkException)
        {
        }
    }

    private async Task WaitForFrameworkTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_frameworkTasksGate)
                tasks = [.. _frameworkTasks];

            if (tasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException or SocketException)
            {
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Task task, long deadline)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return true;
        }

        var remaining = GetRemaining(deadline);
        if (remaining <= TimeSpan.Zero)
            return false;
        try
        {
            await task.WaitAsync(remaining).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static long AddStopwatchDuration(long timestamp, TimeSpan duration)
    {
        var delta = duration.TotalSeconds * Stopwatch.Frequency;
        if (delta >= long.MaxValue - timestamp)
            return long.MaxValue;
        return timestamp + (long)Math.Ceiling(delta);
    }

    private static TimeSpan GetRemaining(long deadline)
    {
        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
    }

    private static async Task DisposeServicesWhenDrainedAsync(
        Task callsDrained,
        ServerServiceCleanup serviceCleanup,
        ILogger logger)
    {
        try
        {
            await callsDrained.ConfigureAwait(false);
            await serviceCleanup.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(logger, "Services", exception);
        }
    }

    private static async Task ObserveShutdownAndDisposeTokensAsync(
        Task shutdownTask,
        CancellationTokenSource acceptCts,
        CancellationTokenSource forceStopCts,
        ILogger logger)
    {
        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(logger, "Framework", exception);
        }
        finally
        {
            acceptCts.Dispose();
            forceStopCts.Dispose();
        }
    }

    private static async Task ObserveCleanupFailureAsync(Task cleanupTask, ILogger logger, string cleanupName)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(logger, cleanupName, exception);
        }
    }

    private SharpLinkCallContextSnapshot CreateCallContext(
        IRpcSession session,
        SharpLinkAuthenticationContext? authenticationContext,
        IRpcStub stub,
        long methodId,
        long requestId,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (_serverInterceptors.Length == 0)
            return new SharpLinkCallContextSnapshot(session.Id, authenticationContext, deadline, metadata);

        return CreateServerInvocationContext(
            session,
            stub,
            methodId,
            requestId,
            authenticationContext,
            deadline,
            metadata,
            cancellationToken);
    }

    private static SharpLinkServerInvocationContext CreateServerInvocationContext(
        IRpcSession session,
        IRpcStub stub,
        long methodId,
        long requestId,
        SharpLinkAuthenticationContext? authenticationContext,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var method = GetMethodDescriptor(stub, methodId);
        var rpcSession = (RpcSession)session;
        return new SharpLinkServerInvocationContext(
            method,
            requestId,
            session.Id,
            rpcSession.LocalEndPoint,
            rpcSession.RemoteEndPoint,
            authenticationContext,
            deadline,
            metadata,
            cancellationToken);
    }

    private static RpcMethodDescriptor GetMethodDescriptor(IRpcStub stub, long methodId)
    {
        if (!stub.TryGetMethodDescriptor(methodId, out var method))
        {
            method = new RpcMethodDescriptor(
                stub.InterfaceHash,
                methodId,
                RpcMethodKind.Unary,
                HasResponsePayload: false,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
        }
        return method;
    }

    private ServerCallAdmissionResult TryAcquireCall(ServerConnectionState connection)
    {
        if (CurrentState != ServerState.Running)
            return ServerCallAdmissionResult.Unavailable;
        if (!connection.TryAcquireCall(_maxConcurrentCallsPerConnection))
        {
            return connection.LifecycleState == ServerConnectionLifecycleState.Ready
                ? ServerCallAdmissionResult.CapacityExhausted
                : ServerCallAdmissionResult.Unavailable;
        }

        if (CurrentState != ServerState.Running)
        {
            connection.ReleaseCall();
            return ServerCallAdmissionResult.Unavailable;
        }

        if (Interlocked.Increment(ref _globalActiveCalls) <= _globalMaxConcurrentCalls)
            return ServerCallAdmissionResult.Acquired;

        Interlocked.Decrement(ref _globalActiveCalls);
        connection.ReleaseCall();
        return ServerCallAdmissionResult.CapacityExhausted;
    }

    private bool TryAcceptRequest(ServerConnectionState connection, long requestId)
    {
        if (CurrentState != ServerState.Running)
            return false;
        if (!connection.TryRecordAcceptedRequest(requestId))
            return false;
        return CurrentState == ServerState.Running &&
               connection.LifecycleState == ServerConnectionLifecycleState.Ready;
    }

    private void ReleaseCall(ServerConnectionState connection)
    {
        var active = Interlocked.Decrement(ref _globalActiveCalls);
        connection.ReleaseCall();
        if (active == 0 && CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            _callsDrained.TrySetResult(true);
    }

    internal int ActiveCallCountForDiagnostics => Volatile.Read(ref _globalActiveCalls);

    internal ServerStopDiagnosticSnapshot? LastStopDiagnostics
        => Volatile.Read(ref _lastStopDiagnostics);

    private ServerStopDiagnosticSnapshot CaptureStopDiagnostics(int activeCalls)
    {
        var connections = _connections.Values.ToArray();
        var snapshots = new ServerConnectionDiagnosticSnapshot[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            snapshots[index] = connections[index]
                .CaptureStopDiagnostics(_maxConcurrentCallsPerConnection);
        }
        return new ServerStopDiagnosticSnapshot(DateTimeOffset.UtcNow, activeCalls, snapshots);
    }

    private ServerState CurrentState => (ServerState)Volatile.Read(ref _state);

    private void TransitionTo(ServerState state)
        => Interlocked.Exchange(ref _state, (int)state);

    internal void ForceStop()
    {
        try
        {
            _forceStopCts.Cancel();
        }
        catch (ObjectDisposedException) when (CurrentState == ServerState.Stopped)
        {
        }
    }

}
