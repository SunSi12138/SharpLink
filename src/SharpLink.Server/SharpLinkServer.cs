using System.Diagnostics;
using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    IServerTransportListener transportListener,
    FrozenDictionary<long, ServiceRegistration> initialServices,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory,
    SharpLinkRuntimeContext runtimeContext,
    ISharpLinkServerAuthenticator? authenticator = null,
    bool authenticationRequired = false,
    SharpLinkProtocolOptions? protocolOptions = null,
    RpcSessionFlushOptions? rpcSessionFlushOptions = null,
    ISharpLinkServerInterceptor[]? serverInterceptors = null,
    IRpcExceptionMapper? exceptionMapper = null,
    IAsyncDisposable? ownedServiceProvider = null,
    IServiceProvider? serviceProvider = null,
    IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null,
    SharpLinkAdmissionController? admissionController = null,
    ServerShutdownPlan? shutdownPlan = null) : ISharpLinkServer
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
        PerConnectionCapacityExhausted,
        ServerCapacityExhausted
    }

    private readonly SharpLinkRuntimeContext _runtimeContext =
        runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
    private FrozenDictionary<long, ServiceRegistration> _services = initialServices;
    private readonly IServiceProvider _serviceProvider = serviceProvider ??
        throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests =
        staticManifests ?? [];
    private readonly Lock _registryGate = new();
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, Task<SharpLinkAssemblyUnregisterResult>> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SharpLinkDynamicModule, ServiceRegistration[]> _detachedModuleServices = [];
    private long _registryGeneration;
    private readonly ConcurrentDictionary<string, ServerConnectionState> _connections = [];
    private readonly ConcurrentDictionary<ServerConnectionState, byte> _retiredConnections = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();
    private readonly ISharpLinkServerAuthenticator? _authenticator = authenticator;
    private readonly bool _authenticationRequired = authenticationRequired;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly Lock _stateGate = new();
    private readonly FrameworkTaskSupervisor _frameworkTasks =
        CreateFrameworkTaskSupervisor(loggerFactory);
    private readonly TaskCompletionSource<bool> _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;
    private Task? _stopTask;
    private int _state = (int)ServerState.Created;
    private readonly SharpLinkProtocolOptions _protocolOptions =
        (protocolOptions ?? runtimeContext.Protocol).CloneValidated();
    private readonly int _maxConcurrentCallsPerConnection =
        runtimeContext.FlowControl.MaxConcurrentCallsPerConnection;
    private readonly int _maxConcurrentCallsPerServer =
        runtimeContext.FlowControl.MaxConcurrentCallsPerServer;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions = rpcSessionFlushOptions;
    private readonly ISharpLinkServerInterceptor[] _serverInterceptors =
        serverInterceptors is { Length: > 0 } ? [.. serverInterceptors] : [];
    private readonly IRpcExceptionMapper _exceptionMapper =
        exceptionMapper ?? new DefaultRpcExceptionMapper(includeDetails: false);
    private readonly ServerServiceCleanup _serviceCleanup = new(initialServices.Values, ownedServiceProvider);
    private readonly SharpLinkAdmissionController? _admissionController = admissionController;
    private readonly ServerShutdownPlan _shutdownPlan = shutdownPlan ?? ServerShutdownPlan.Default;
    private Task? _deferredServiceCleanupTask;
    private Task? _shutdownCleanupObserver;
    private Task? _serviceCleanupObserver;
    private int _deferredConnectionCleanups;
    private ServerStopDiagnosticSnapshot? _lastStopDiagnostics;
    private int _globalActiveCalls;
    private long _rejectedOneWayCalls;
    private long _oneWayAdmissionLogTimestamp;

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
        var started = Stopwatch.GetTimestamp();
        var gracefulDeadline = AddStopwatchDuration(started, gracefulTimeout);
        var finalDeadline = AddStopwatchDuration(gracefulDeadline, _shutdownPlan.CleanupBudget);
        var faulted = false;
        List<Exception>? stopFailures = null;

        lock (_registryGate)
            TransitionTo(ServerState.Draining);
        _admissionController?.StopAccepting();
        BeginDrainDynamicModules();
        _frameworkTasks.Seal();
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
                _deferredServiceCleanupTask = DisposeServicesWhenDrainedAsync(_callsDrained.Task);
            }

            CancelForShutdown(_forceStopCts, _logger, "CallCancellation");
            var closeSessionsTask = DisposeAllSessionsAsync();
            var frameworkTasksTask = _frameworkTasks.DrainAsync();
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
                AddTaskFailures(ref stopFailures, frameworkCleanupTask, exception);
            }

            if (!frameworkCleanupCompleted)
            {
                faulted = true;
                LogFrameworkCleanupTimeout(_logger, (int)_shutdownPlan.CleanupBudget.TotalSeconds);
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
                var serviceCleanupTask = DisposeRegisteredServicesAsync();
                try
                {
                    if (!await WaitUntilAsync(serviceCleanupTask, finalDeadline).ConfigureAwait(false))
                    {
                        faulted = true;
                        _serviceCleanupObserver = ObserveCleanupFailureAsync(
                            serviceCleanupTask,
                            _logger,
                            "Services");
                    }
                }
                catch (Exception exception)
                {
                    faulted = true;
                    LogDeferredCleanupFailed(_logger, "Services", exception);
                    AddTaskFailures(ref stopFailures, serviceCleanupTask, exception);
                }
            }
        }
        catch (Exception exception)
        {
            faulted = true;
            LogDeferredCleanupFailed(_logger, "Stop", exception);
            (stopFailures ??= []).Add(exception);
        }

        TransitionTo(faulted ? ServerState.Faulted : ServerState.Stopped);
        ThrowStopFailures(stopFailures);
    }

    private static void AddTaskFailures(
        ref List<Exception>? failures,
        Task task,
        Exception fallback)
    {
        if (task.Exception is not { } aggregate)
        {
            (failures ??= []).Add(fallback);
            return;
        }

        foreach (var exception in aggregate.Flatten().InnerExceptions)
            (failures ??= []).Add(exception);
    }

    private static void ThrowStopFailures(List<Exception>? failures)
    {
        if (failures is null)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private async Task CleanupAfterRunFailureAsync()
    {
        var deadline = AddStopwatchDuration(
            Stopwatch.GetTimestamp(),
            _shutdownPlan.CleanupBudget);

        CancelForShutdown(_acceptCts, _logger, "AcceptCancellation");
        _admissionController?.StopAccepting();
        BeginDrainDynamicModules();
        _frameworkTasks.Seal();
        CancelForShutdown(_forceStopCts, _logger, "CallCancellation");
        if (Volatile.Read(ref _globalActiveCalls) == 0)
            _callsDrained.TrySetResult(true);
        else
        {
            var unfinishedCalls = Volatile.Read(ref _globalActiveCalls);
            LogForcedCallsRemaining(_logger, unfinishedCalls);
            SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
            _deferredServiceCleanupTask ??= DisposeServicesWhenDrainedAsync(_callsDrained.Task);
        }

        var frameworkCleanupTask = Task.WhenAll(
            StartListenerDispose(transportListener),
            DisposeAllSessionsAsync(),
            _frameworkTasks.DrainAsync());
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
            LogFrameworkCleanupTimeout(_logger, (int)_shutdownPlan.CleanupBudget.TotalSeconds);
            _shutdownCleanupObserver = ObserveShutdownAndDisposeTokensAsync(
                frameworkCleanupTask,
                _acceptCts,
                _forceStopCts,
                _logger);
        }

        if (_callsDrained.Task.IsCompletedSuccessfully)
        {
            var serviceCleanupTask = DisposeRegisteredServicesAsync();
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

    internal void TrackFrameworkTask(
        Task task,
        string operation,
        TaskObservationMode observationMode = TaskObservationMode.FrameworkOwned)
        => _frameworkTasks.Track(task, operation, observationMode, IsExpectedSessionShutdownException);

    private async Task DisposeAllSessionsAsync()
    {
        var connections = _connections.Values.ToArray();
        var tasks = new Task[connections.Length];
        for (var index = 0; index < connections.Length; index++)
            tasks[index] = DisconnectConnectionAsync(connections[index]).AsTask();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            ThrowUnexpectedShutdownTaskFailures(tasks);
        }
    }

    private static bool IsExpectedSessionShutdownException(Exception exception)
        => exception is OperationCanceledException or ObjectDisposedException or
            System.IO.IOException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };

    private static void ThrowUnexpectedShutdownTaskFailures(Task[] tasks)
    {
        List<Exception>? unexpected = null;
        for (var taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
        {
            if (tasks[taskIndex].Exception is not { } aggregate)
                continue;
            foreach (var exception in aggregate.Flatten().InnerExceptions)
            {
                if (IsExpectedSessionShutdownException(exception))
                    continue;
                (unexpected ??= []).Add(exception);
            }
        }

        if (unexpected is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
        if (unexpected is not null)
            throw new AggregateException(unexpected);
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
        return await SharpLinkTimer.WaitAsync(task, remaining).ConfigureAwait(false);
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

    private async Task DisposeServicesWhenDrainedAsync(Task callsDrained)
    {
        try
        {
            await callsDrained.ConfigureAwait(false);
            await DisposeRegisteredServicesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDeferredCleanupFailed(_logger, "Services", exception);
        }
    }

    private async Task DisposeRegisteredServicesAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await ReleaseDrainedDynamicModulesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await _serviceCleanup.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (_admissionController is not null)
        {
            try
            {
                await _admissionController.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            _runtimeContext.Dispose();
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
        ServerConnectionState connection,
        IRpcStub stub,
        long methodId,
        long requestId,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var session = connection.Session;
        if (_serverInterceptors.Length == 0)
            return connection.GetCallContextSnapshot(deadline, metadata);

        return CreateServerInvocationContext(
            session,
            stub,
            methodId,
            requestId,
            connection.AuthenticationContext,
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
                ? ServerCallAdmissionResult.PerConnectionCapacityExhausted
                : ServerCallAdmissionResult.Unavailable;
        }

        if (Interlocked.Increment(ref _globalActiveCalls) > _maxConcurrentCallsPerServer)
        {
            Interlocked.Decrement(ref _globalActiveCalls);
            connection.ReleaseCall();
            return ServerCallAdmissionResult.ServerCapacityExhausted;
        }

        if (CurrentState == ServerState.Running)
            return ServerCallAdmissionResult.Acquired;

        ReleaseCall(connection);
        return ServerCallAdmissionResult.Unavailable;
    }

    private static string GetCallCapacityExhaustionReason(ServerCallAdmissionResult result)
        => result switch
        {
            ServerCallAdmissionResult.PerConnectionCapacityExhausted =>
                SharpLinkResourceExhaustion.PerConnectionCallCapacity,
            ServerCallAdmissionResult.ServerCapacityExhausted =>
                SharpLinkResourceExhaustion.ServerCallCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "A capacity result is required.")
        };

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

    internal int MaxConcurrentCallsPerConnectionForDiagnostics => _maxConcurrentCallsPerConnection;

    internal int MaxConcurrentCallsPerServerForDiagnostics => _maxConcurrentCallsPerServer;

    internal ServerStopDiagnosticSnapshot? LastStopDiagnostics
        => Volatile.Read(ref _lastStopDiagnostics);

    internal FrameworkTaskSupervisorSnapshot FrameworkTaskSnapshotForDiagnostics
        => _frameworkTasks.CaptureSnapshot();

    internal ServerShutdownPlan ShutdownPlanForDiagnostics => _shutdownPlan;

    internal ServerDeferredTaskDiagnosticSnapshot DeferredTaskSnapshotForDiagnostics
        => new(
            Volatile.Read(ref _deferredServiceCleanupTask)?.Status,
            Volatile.Read(ref _shutdownCleanupObserver)?.Status,
            Volatile.Read(ref _serviceCleanupObserver)?.Status,
            Volatile.Read(ref _deferredConnectionCleanups));

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

    private static FrameworkTaskSupervisor CreateFrameworkTaskSupervisor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var frameworkLogger = loggerFactory.CreateLogger<SharpLinkServer>();
        return new FrameworkTaskSupervisor((operation, exception) =>
            LogServerBackgroundLoopUnhandledException(frameworkLogger, operation, exception));
    }

}
