using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer : ISharpLinkServer
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

    private readonly IServerTransportListener _transportListener;
    private readonly TimeSpan _heartbeatCheckInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly SharpLinkRuntimeContext _runtimeContext;
    private readonly ServerServiceModuleRegistry _serviceModuleRegistry;
    private readonly ServerConnectionRegistry _connectionRegistry = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
    private readonly ILogger _logger;
    private readonly ServerAuthenticationCoordinator _authentication;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly Lock _stateGate = new();
    private readonly FrameworkTaskSupervisor _frameworkTasks;
    private readonly TaskCompletionSource<bool> _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;
    private Task? _stopTask;
    private int _state = (int)ServerState.Created;
    private readonly SharpLinkProtocolOptions _protocolOptions;
    private readonly int _maxConcurrentCallsPerConnection;
    private readonly int _maxConcurrentCallsPerServer;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private ISharpLinkServerInterceptor[] _serverInterceptors;
    private readonly IRpcExceptionMapper _exceptionMapper;
    private readonly ServerServiceCleanup _serviceCleanup;
    private readonly SharpLinkAdmissionController? _admissionController;
    private readonly ServerConnectionAdmission _connectionAdmission;
    private readonly ServerCallAdmission _callAdmission;
    private readonly ServerShutdownPlan _shutdownPlan;
    private Task? _deferredServiceCleanupTask;
    private Task? _shutdownCleanupObserver;
    private Task? _serviceCleanupObserver;
    private int _deferredConnectionCleanups;
    private ServerStopDiagnosticSnapshot? _lastStopDiagnostics;
    // 0 = no signal, 1 = single winner recording, 2 = snapshot published before TCS completion.
    private int _callDrainSignalState;
    private int _lastCallDrainSignalGlobalCalls;
    private int _lastCallDrainSignalPendingAdmissions;
    private int _lastCallDrainSignalLocalCalls;
    private long _rejectedOneWayCalls;
    private FixedWindowLogThrottle _connectionAdmissionLogThrottle;
    private FixedWindowLogThrottle _oneWayAdmissionLogThrottle;
    private FixedWindowLogThrottle _protocolViolationLogThrottle;

    // Keep the established partial-file transaction shape while the mutable registry state itself
    // is owned by the focused collaborator. These are non-owning aliases, not Server fields.
    private ref FrozenDictionary<long, ServiceRegistration> _services
        => ref _serviceModuleRegistry.ServicesStorage;
    private Lock _registryGate => _serviceModuleRegistry.Gate;
    private ServerServiceModuleRegistry.DynamicModuleTable _dynamicModules
        => _serviceModuleRegistry.DynamicModules;
    private ServerServiceModuleRegistry.UnregisterOperationTable _unregisterOperations
        => _serviceModuleRegistry.UnregisterOperations;
    private ServerServiceModuleRegistry.DetachedModuleServiceTable _detachedModuleServices
        => _serviceModuleRegistry.DetachedModuleServices;
    private ref long _registryGeneration => ref _serviceModuleRegistry.GenerationStorage;

    /// <summary>
    /// Initializes a Server from the explicit composition materialized by
    /// <see cref="SharpLinkServerBuilder"/>. It performs no mutable-option fallback, clone, catalog
    /// lookup, listener/service-provider/FrameworkTaskSupervisor creation, or RuntimeContext materialization.
    /// </summary>
    internal SharpLinkServer(ServerRuntimeComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _transportListener = composition.TransportListener;
        _serviceModuleRegistry = new ServerServiceModuleRegistry(composition.Services);
        _heartbeatCheckInterval = composition.HeartbeatCheckInterval;
        _heartbeatTimeout = composition.HeartbeatTimeout;
        _logger = composition.Logger;
        _runtimeContext = composition.RuntimeContext;
        _authentication = composition.Authentication;
        _protocolOptions = composition.ProtocolOptions;
        _rpcSessionFlushOptions = composition.RpcSessionFlushOptions;
        _serverInterceptors = composition.Interceptors;
        _exceptionMapper = composition.ExceptionMapper;
        _serviceProvider = composition.ServiceProvider;
        _staticManifests = composition.StaticManifests;
        _admissionController = composition.AdmissionController;
        _connectionAdmission = composition.ConnectionAdmission;
        _shutdownPlan = composition.ShutdownPlan;
        _maxConcurrentCallsPerConnection = _runtimeContext.FlowControl.MaxConcurrentCallsPerConnection;
        _maxConcurrentCallsPerServer = _runtimeContext.FlowControl.MaxConcurrentCallsPerServer;
        _callAdmission = new ServerCallAdmission(
            this,
            _maxConcurrentCallsPerConnection,
            _maxConcurrentCallsPerServer);
        _serviceCleanup = composition.ServiceCleanup;
        _frameworkTasks = composition.FrameworkTasks;
        var logWindow = TimeSpan.FromSeconds(5);
        var timestampFrequency = _runtimeContext.TimeProvider.TimestampFrequency;
        _connectionAdmissionLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
        _oneWayAdmissionLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
        _protocolViolationLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
    }

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
        var started = _runtimeContext.TimeProvider.GetTimestamp();
        var gracefulDeadline = SharpLinkTime.AddDuration(
            started,
            gracefulTimeout,
            _runtimeContext.TimeProvider.TimestampFrequency);
        var finalDeadline = SharpLinkTime.AddDuration(
            gracefulDeadline,
            _shutdownPlan.CleanupBudget,
            _runtimeContext.TimeProvider.TimestampFrequency);
        var faulted = false;
        List<Exception>? stopFailures = null;

        lock (_registryGate)
            TransitionTo(ServerState.Draining);
        _admissionController?.StopAccepting();
        BeginDrainDynamicModules();
        _frameworkTasks.Seal();
        CancelForShutdown(_acceptCts, _logger, "AcceptCancellation");
        var listenerDisposeTask = StartListenerDispose(_transportListener);
        var goAwayTask = SendGoAwayToAllAsync();

        try
        {
            TrySignalCallsDrained();
            if (!_callsDrained.Task.IsCompletedSuccessfully)
                await WaitUntilWithRuntimeTimeAsync(_callsDrained.Task, gracefulDeadline).ConfigureAwait(false);

            var callsDrained = _callsDrained.Task.IsCompletedSuccessfully;
            Task flushTask = Task.CompletedTask;
            if (callsDrained)
                flushTask = FlushAllSessionsAsync();

            var unfinishedCalls = _callAdmission.ActiveCallCount;
            if (!callsDrained)
            {
                if (unfinishedCalls > 0)
                {
                    Volatile.Write(ref _lastStopDiagnostics, CaptureStopDiagnostics(unfinishedCalls));
                    LogForcedCallsRemaining(_logger, unfinishedCalls);
                    SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
                }

                // A pending admission is not a user-call metric, but it must retain
                // the service graph until it either publishes a global slot or rolls
                // back its local slot.
                _deferredServiceCleanupTask ??= DisposeServicesWhenDrainedAsync(_callsDrained.Task);
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
                frameworkCleanupCompleted = await WaitUntilWithRuntimeTimeAsync(
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

            if (callsDrained)
            {
                var serviceCleanupTask = DisposeRegisteredServicesAsync();
                try
                {
                    if (!await WaitUntilWithRuntimeTimeAsync(serviceCleanupTask, finalDeadline).ConfigureAwait(false))
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
        var deadline = SharpLinkTime.AddDuration(
            _runtimeContext.TimeProvider.GetTimestamp(),
            _shutdownPlan.CleanupBudget,
            _runtimeContext.TimeProvider.TimestampFrequency);

        CancelForShutdown(_acceptCts, _logger, "AcceptCancellation");
        _admissionController?.StopAccepting();
        BeginDrainDynamicModules();
        _frameworkTasks.Seal();
        CancelForShutdown(_forceStopCts, _logger, "CallCancellation");
        TrySignalCallsDrained();
        var callsDrained = _callsDrained.Task.IsCompletedSuccessfully;
        if (!callsDrained)
        {
            var unfinishedCalls = _callAdmission.ActiveCallCount;
            if (unfinishedCalls > 0)
            {
                LogForcedCallsRemaining(_logger, unfinishedCalls);
                SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
            }
            _deferredServiceCleanupTask ??= DisposeServicesWhenDrainedAsync(_callsDrained.Task);
        }

        var frameworkCleanupTask = Task.WhenAll(
            StartListenerDispose(_transportListener),
            DisposeAllSessionsAsync(),
            _frameworkTasks.DrainAsync());
        var frameworkCleanupCompleted = false;
        try
        {
            frameworkCleanupCompleted = await WaitUntilWithRuntimeTimeAsync(frameworkCleanupTask, deadline)
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

        if (callsDrained)
        {
            var serviceCleanupTask = DisposeRegisteredServicesAsync();
            try
            {
                if (!await WaitUntilWithRuntimeTimeAsync(serviceCleanupTask, deadline).ConfigureAwait(false))
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
        var connections = _connectionRegistry.SnapshotActive();
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
        var connections = _connectionRegistry.SnapshotActive();
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

    /// <summary>Exposes the pre-call connection admission gate for diagnostics and tests.</summary>
    internal ServerConnectionAdmission ConnectionAdmission => _connectionAdmission;

    internal void RecordConnectionAdmissionRejection(string reason)
    {
        SharpLinkTelemetry.RecordConnectionRejected(reason);
        if (ShouldLogConnectionAdmissionRejection())
            LogConnectionAdmissionRejected(_logger, reason);
    }

    private bool ShouldLogConnectionAdmissionRejection()
        => _connectionAdmissionLogThrottle.ShouldLog(
            _runtimeContext.TimeProvider.GetTimestamp(),
            out _);

    private async Task DisposeAllSessionsAsync()
    {
        var connections = _connectionRegistry.SnapshotActive();
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

    private Task<bool> WaitUntilWithRuntimeTimeAsync(Task task, long deadline)
        => WaitUntilWithProviderAsync(task, deadline, _runtimeContext.TimeProvider);

    private static async Task<bool> WaitUntilWithProviderAsync(
        Task task,
        long deadline,
        TimeProvider timeProvider)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return true;
        }

        var remaining = SharpLinkTime.GetRemaining(
            deadline,
            timeProvider.GetTimestamp(),
            timeProvider.TimestampFrequency);
        if (remaining <= TimeSpan.Zero)
            return false;
        return await SharpLinkTimer.WaitAsync(
            task,
            remaining,
            timeProvider).ConfigureAwait(false);
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
        RpcDeadline deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var session = connection.Session;
        var interceptors = Volatile.Read(ref _serverInterceptors);
        if (interceptors.Length == 0)
            return connection.GetCallContextSnapshot(deadline, metadata);

        var method = GetMethodDescriptor(stub, methodId);
        if (method.Kind != RpcMethodKind.OneWay)
        {
            ReservePreInvocationRequestStreams(
                session,
                method.ClientStreamCount,
                requestId,
                cancellationToken);
        }

        return CreateServerInvocationContext(
            session,
            stub,
            methodId,
            requestId,
            connection.AuthenticationContext,
            deadline,
            _runtimeContext.TimeProvider,
            metadata,
            cancellationToken,
            interceptors);
    }

    private static SharpLinkServerInvocationContext CreateServerInvocationContext(
        RpcSession session,
        IRpcStub stub,
        long methodId,
        long requestId,
        SharpLinkAuthenticationContext? authenticationContext,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken,
        ISharpLinkServerInterceptor[]? interceptors = null)
    {
        var method = GetMethodDescriptor(stub, methodId);
        return new SharpLinkServerInvocationContext(
            method,
            requestId,
            session.Id,
            session.LocalEndPoint,
            session.RemoteEndPoint,
            authenticationContext,
            deadline,
            deadlineTimeProvider,
            metadata,
            cancellationToken,
            interceptors);
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

    internal ServerCallAdmissionResult TryAcquireCall(ServerConnectionState connection)
        => _callAdmission.TryAcquireCall(connection);

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

    internal void ReleaseCall(ServerConnectionState connection)
        => _callAdmission.ReleaseCall(connection);

    private void TrySignalCallsDrained(ServerConnectionState? releasingConnection = null)
    {
        if (CurrentState is not (ServerState.Draining or ServerState.Stopped or ServerState.Faulted))
            return;

        // A pending admission stays counted until it has either published its
        // global slot or fully released both provisional slots. Reading it first
        // makes a zero global count safe: a post-stop entrant may still increment
        // pending, but its second state check prevents it from taking any slot.
        var pendingAdmissions = _callAdmission.PendingCallAdmissions;
        if (pendingAdmissions != 0)
            return;

        var globalActiveCalls = _callAdmission.ActiveCallCount;
        if (globalActiveCalls != 0)
        {
            return;
        }

        var releasingConnectionActiveCalls = releasingConnection?.ActiveCalls ?? 0;
        if (releasingConnection is not null && releasingConnectionActiveCalls != 0)
        {
            throw new InvalidOperationException(
                "Server drain cannot complete before the releasing connection publishes its local call release.");
        }

        // There is one publication winner. It records every observed counter with
        // release ordering before completing the TCS, so a continuation that sees
        // calls drained can read a stable, non-forgeable terminal snapshot.
        if (Interlocked.CompareExchange(ref _callDrainSignalState, 1, 0) != 0)
            return;

        Volatile.Write(ref _lastCallDrainSignalGlobalCalls, globalActiveCalls);
        Volatile.Write(ref _lastCallDrainSignalPendingAdmissions, pendingAdmissions);
        Volatile.Write(ref _lastCallDrainSignalLocalCalls, releasingConnectionActiveCalls);
        Volatile.Write(ref _callDrainSignalState, 2);
        _callsDrained.TrySetResult(true);
    }

    internal int ActiveCallCountForDiagnostics => _callAdmission.ActiveCallCount;

    internal int PendingCallAdmissionsForDiagnostics => _callAdmission.PendingCallAdmissions;

    internal Task<bool> CallsDrainedForDiagnostics => _callsDrained.Task;

    internal ServerCallDrainSignalSnapshot? LastCallDrainSignalForDiagnostics
    {
        get
        {
            if (Volatile.Read(ref _callDrainSignalState) != 2)
                return null;
            return new ServerCallDrainSignalSnapshot(
                Volatile.Read(ref _lastCallDrainSignalGlobalCalls),
                Volatile.Read(ref _lastCallDrainSignalPendingAdmissions),
                Volatile.Read(ref _lastCallDrainSignalLocalCalls));
        }
    }

    internal void AssertCallAccountingInvariant()
    {
        if (ActiveCallCountForDiagnostics < 0)
            throw new InvalidOperationException("Server global active call count became negative.");
        if (PendingCallAdmissionsForDiagnostics < 0)
            throw new InvalidOperationException("Server pending call admission count became negative.");
        // A thread that read Running before Stop can increment the transient pending
        // counter after drain is already published, but its second state check cannot
        // acquire a local or global slot. Therefore completed drain proves no active
        // call slot remains; a stable caller that also needs pending == 0 must join
        // its admission work before asserting that stronger condition.
        if (_callsDrained.Task.IsCompletedSuccessfully && ActiveCallCountForDiagnostics != 0)
        {
            throw new InvalidOperationException(
                "Server call drain completed before global active calls reached zero.");
        }
    }

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
        var connections = _connectionRegistry.SnapshotActive();
        var snapshots = new ServerConnectionDiagnosticSnapshot[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            snapshots[index] = connections[index]
                .CaptureStopDiagnostics(_maxConcurrentCallsPerConnection);
        }
        return new ServerStopDiagnosticSnapshot(
            _runtimeContext.TimeProvider.GetUtcNow(),
            activeCalls,
            snapshots);
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

    internal static FrameworkTaskSupervisor CreateFrameworkTaskSupervisor(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new FrameworkTaskSupervisor((operation, exception) =>
            LogServerBackgroundLoopUnhandledException(logger, operation, exception));
    }
}
