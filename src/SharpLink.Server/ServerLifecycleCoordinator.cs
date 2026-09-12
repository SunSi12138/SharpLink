namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Owns the one-shot server lifecycle state machine. The outer server supplies composed
    /// dependencies and request/connection operations; this coordinator owns stop idempotency,
    /// drain publication, shutdown cancellation, bounded framework teardown, and final cleanup order.
    ///
    /// Invariants:
    /// - exactly one startup operation, one accept runtime, and one shared stop/cleanup task are established;
    /// - the first stop owner fixes the graceful deadline for every later waiter;
    /// - Draining is published before admission/framework intake is closed;
    /// - call drain is published only after pending admission and global call ownership reach zero;
    /// - server services are never disposed before call drain completes;
    /// - framework teardown is bounded, while user-owned service cleanup may remain deferred.
    /// </summary>
    internal sealed partial class ServerLifecycleCoordinator
    {
        private readonly SharpLinkServer _server;
        private readonly CancellationTokenSource _acceptCts = new();
        private readonly CancellationTokenSource _forceStopCts = new();
        private readonly Lock _stateGate = new();
        private readonly TaskCompletionSource<bool> _callsDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _terminalCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _startTask;
        private Task? _acceptTask;
        private Task? _acceptObserverTask;
        private Task? _terminalFailureObserverTask;
        private Task? _stopTask;
        private Exception? _terminalFailure;
        private Task? _deferredServiceCleanupTask;
        private Task? _shutdownCleanupObserver;
        private Task? _serviceCleanupObserver;
        private ServerStopDiagnosticSnapshot? _lastStopDiagnostics;
        // 0 = no signal, 1 = single winner recording, 2 = snapshot published before TCS completion.
        private int _callDrainSignalState;
        private int _lastCallDrainSignalGlobalCalls;
        private int _lastCallDrainSignalPendingAdmissions;
        private int _lastCallDrainSignalLocalCalls;

        internal ServerLifecycleCoordinator(SharpLinkServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        internal CancellationTokenSource AcceptSource => _acceptCts;

        internal CancellationTokenSource ForceStopSource => _forceStopCts;

        internal Lock StateGate => _stateGate;

        // Read under StateGate so lifecycle consumers serialize against stop publication.
        internal bool HasStopStarted => _stopTask is not null;

        internal SharpLinkServerLifecycleState LifecycleState => _server.CurrentState switch
        {
            ServerState.Created => SharpLinkServerLifecycleState.Created,
            ServerState.Starting => SharpLinkServerLifecycleState.Starting,
            ServerState.Running => SharpLinkServerLifecycleState.Running,
            ServerState.Draining => SharpLinkServerLifecycleState.Draining,
            ServerState.Stopped => SharpLinkServerLifecycleState.Stopped,
            ServerState.Faulted => SharpLinkServerLifecycleState.Faulted,
            _ => SharpLinkServerLifecycleState.Faulted
        };

        internal SharpLinkHealthStatus HealthStatus => _server.CurrentState switch
        {
            ServerState.Running => SharpLinkHealthStatus.Ready,
            ServerState.Draining => SharpLinkHealthStatus.Draining,
            _ => SharpLinkHealthStatus.Unhealthy
        };

        internal ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task operation;
            TaskCompletionSource<bool>? startCompletion = null;
            lock (_stateGate)
            {
                var state = _server.CurrentState;
                if (state == ServerState.Running)
                    return ValueTask.CompletedTask;
                if (state is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                {
                    return ValueTask.FromException(new SharpLinkException(
                        SharpLinkErrorCode.ConnectionClosed,
                        $"Server lifecycle state '{state}' cannot start."));
                }

                if (state == ServerState.Starting)
                {
                    operation = _startTask ??
                        throw new InvalidOperationException("Server startup has no owned start operation.");
                }
                else
                {
                    _server.TransitionTo(ServerState.Starting);
                    startCompletion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _startTask = startCompletion.Task;
                    operation = _startTask;
                }
            }

            if (startCompletion is not null)
            {
                _ = CompleteStartAsync(startCompletion, cancellationToken);
                return new ValueTask(operation);
            }

            return cancellationToken.CanBeCanceled
                ? new ValueTask(operation.WaitAsync(cancellationToken))
                : new ValueTask(operation);
        }

        internal Task WaitForShutdownAsync(CancellationToken cancellationToken)
        {
            var completion = _terminalCompletion.Task;
            return cancellationToken.CanBeCanceled
                ? completion.WaitAsync(cancellationToken)
                : completion;
        }

        internal ValueTask StopAsync(
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
            var stopTask = GetOrCreateStopTask(gracefulTimeout);
            return cancellationToken.CanBeCanceled
                ? new ValueTask(stopTask.WaitAsync(cancellationToken))
                : new ValueTask(stopTask);
        }

        private async Task CompleteStartAsync(
            TaskCompletionSource<bool> completion,
            CancellationToken startupCancellation)
        {
            try
            {
                await StartCoreAsync(startupCancellation).ConfigureAwait(false);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception) when (startupCancellation.IsCancellationRequested)
            {
                try
                {
                    await StopAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                    completion.TrySetCanceled(startupCancellation);
                }
                catch (Exception cleanupException)
                {
                    completion.TrySetException(new AggregateException(exception, cleanupException));
                }
            }
            catch (Exception exception)
            {
                try
                {
                    await BeginTerminalFailure(exception).ConfigureAwait(false);
                    completion.TrySetException(exception);
                }
                catch (Exception terminalException)
                {
                    completion.TrySetException(terminalException);
                }
            }
        }

        private async Task StartCoreAsync(CancellationToken startupCancellation)
        {
            var acceptStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var running = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptToken = _acceptCts.Token;

            var acceptTask = _server.RunAcceptLoopAsync(acceptStarted, running, acceptToken);
            Volatile.Write(ref _acceptTask, acceptTask);

            try
            {
                await acceptStarted.Task.WaitAsync(startupCancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                acceptToken.IsCancellationRequested && !startupCancellation.IsCancellationRequested)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed,
                    "Server stopped while startup was in progress.");
            }

            startupCancellation.ThrowIfCancellationRequested();
            var previous = (ServerState)Interlocked.CompareExchange(
                ref _server._state,
                (int)ServerState.Running,
                (int)ServerState.Starting);
            if (previous != ServerState.Starting)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ConnectionClosed,
                    $"Server startup was interrupted by lifecycle state '{previous}'.");
            }
            running.TrySetResult(true);

            if (acceptTask.IsCompleted)
            {
                await acceptTask.ConfigureAwait(false);
                throw new InvalidOperationException("Server accept loop completed during startup.");
            }

            Volatile.Write(ref _acceptObserverTask, ObserveAcceptLoopAsync(acceptTask, acceptToken));
        }

        private async Task ObserveAcceptLoopAsync(Task acceptTask, CancellationToken acceptToken)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
                if (_server.CurrentState is ServerState.Starting or ServerState.Running)
                {
                    BeginAndObserveTerminalFailure(new InvalidOperationException(
                        "Server accept loop completed unexpectedly."));
                }
            }
            catch (OperationCanceledException) when (acceptToken.IsCancellationRequested)
            {
            }
            catch (Exception) when (
                _server.CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
            }
            catch (Exception exception)
            {
                BeginAndObserveTerminalFailure(exception);
            }
        }

        internal void BeginAndObserveTerminalFailure(Exception failure)
        {
            var stopTask = BeginTerminalFailure(failure);
            lock (_stateGate)
                _terminalFailureObserverTask ??= ObserveTerminalFailureAsync(stopTask);
        }

        internal Task? TerminalFailureObserverTaskForDiagnostics
            => Volatile.Read(ref _terminalFailureObserverTask);

        private static async Task ObserveTerminalFailureAsync(Task stopTask)
        {
            try
            {
                await stopTask.ConfigureAwait(false);
            }
            catch
            {
                // Observation is intentionally separate from propagation: the shared stop task stays
                // faulted so a later StopAsync caller can still join and rethrow the terminal failure.
            }
        }

        internal Task BeginTerminalFailure(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (_stateGate)
            {
                var state = _server.CurrentState;
                if (state is ServerState.Draining or ServerState.Stopped)
                    return _stopTask ?? Task.CompletedTask;

                _terminalFailure ??= failure;
                if (state != ServerState.Faulted)
                    _server.TransitionTo(ServerState.Faulted);
                return _stopTask ??= CompleteFaultedStopAsync();
            }
        }

        private async Task CompleteFaultedStopAsync()
        {
            Exception? cleanupFailure = null;
            try
            {
                await CleanupAfterRuntimeFailureAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            Exception terminalFailure;
            lock (_stateGate)
            {
                var primaryFailure = _terminalFailure ?? new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "Server entered Faulted without a recorded terminal failure.");
                terminalFailure = cleanupFailure is null
                    ? primaryFailure
                    : new AggregateException(primaryFailure, cleanupFailure);
            }

            Volatile.Write(ref _terminalFailure, terminalFailure);
            _terminalCompletion.TrySetException(terminalFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }

        private async Task WaitForAcceptRuntimeShutdownAsync()
        {
            var observer = Volatile.Read(ref _acceptObserverTask);
            if (observer is not null)
            {
                await observer.ConfigureAwait(false);
                return;
            }

            var acceptTask = Volatile.Read(ref _acceptTask);
            if (acceptTask is null)
                return;
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_acceptCts.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (
                _server.CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
            }
            catch (Exception) when (_server.CurrentState == ServerState.Faulted)
            {
                // The raw accept failure is already recorded as the terminal failure.
            }
        }

        private Task GetOrCreateStopTask(TimeSpan gracefulTimeout)
        {
            lock (_stateGate)
            {
                _stopTask ??= StopCoreAsync(gracefulTimeout);
                return _stopTask;
            }
        }

        private async Task StopCoreAsync(TimeSpan gracefulTimeout)
        {
            var timeProvider = _server._runtimeContext.TimeProvider;
            var started = timeProvider.GetTimestamp();
            var gracefulDeadline = SharpLinkTime.AddDuration(
                started,
                gracefulTimeout,
                timeProvider.TimestampFrequency);
            var finalDeadline = SharpLinkTime.AddDuration(
                gracefulDeadline,
                _server._shutdownPlan.CleanupBudget,
                timeProvider.TimestampFrequency);
            var faulted = false;
            List<Exception>? stopFailures = null;

            lock (_server._registryGate)
                _server.TransitionTo(ServerState.Draining);
            _server._admissionController?.StopAccepting();
            _server.BeginDrainDynamicModules();
            _server._frameworkTasks.Seal();
            CancelForShutdown(_acceptCts, "AcceptCancellation");
            var listenerDisposeTask = StartListenerDispose(_server._transportListener);
            var goAwayTask = SendGoAwayToAllAsync();

            try
            {
                TrySignalCallsDrained();
                if (!_callsDrained.Task.IsCompletedSuccessfully)
                {
                    await WaitUntilWithRuntimeTimeAsync(
                        _callsDrained.Task,
                        gracefulDeadline).ConfigureAwait(false);
                }

                var callsDrained = _callsDrained.Task.IsCompletedSuccessfully;
                Task flushTask = Task.CompletedTask;
                if (callsDrained)
                    flushTask = FlushAllSessionsAsync();

                var unfinishedCalls = _server._callAdmission.ActiveCallCount;
                if (!callsDrained)
                {
                    if (unfinishedCalls > 0)
                    {
                        Volatile.Write(
                            ref _lastStopDiagnostics,
                            _server.CaptureStopDiagnostics(unfinishedCalls));
                        SharpLinkServer.LogForcedCallsRemaining(_server._logger, unfinishedCalls);
                        SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
                    }

                    // Pending admission is not a user-call metric, but it retains the service graph
                    // until the provisional local/global ownership transfer is fully resolved.
                    _deferredServiceCleanupTask ??=
                        DisposeServicesWhenDrainedAsync(_callsDrained.Task);
                }

                CancelForShutdown(_forceStopCts, "CallCancellation");
                var closeSessionsTask = DisposeAllSessionsAsync();
                var frameworkTasksTask = _server._frameworkTasks.DrainAsync();
                var frameworkCleanupTask = Task.WhenAll(
                    listenerDisposeTask,
                    goAwayTask,
                    flushTask,
                    closeSessionsTask,
                    WaitForAcceptRuntimeShutdownAsync(),
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
                    SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Framework", exception);
                    AddTaskFailures(ref stopFailures, frameworkCleanupTask, exception);
                }

                if (!frameworkCleanupCompleted)
                {
                    Console.Error.WriteLine($"STOP_FRAMEWORK_TIMEOUT listener={listenerDisposeTask.Status}; goAway={goAwayTask.Status}; flush={flushTask.Status}; close={closeSessionsTask.Status}; accept={_acceptObserverTask?.Status}; supervisor={frameworkTasksTask.Status}; aggregate={frameworkCleanupTask.Status}; supervisorSnapshot={System.Text.Json.JsonSerializer.Serialize(_server.FrameworkTaskSnapshotForDiagnostics)}");
                    faulted = true;
                    SharpLinkServer.LogFrameworkCleanupTimeout(
                        _server._logger,
                        (int)_server._shutdownPlan.CleanupBudget.TotalSeconds);
                    _shutdownCleanupObserver = ObserveShutdownAndDisposeTokensAsync(frameworkCleanupTask);
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
                        if (!await WaitUntilWithRuntimeTimeAsync(
                                serviceCleanupTask,
                                finalDeadline).ConfigureAwait(false))
                        {
                            Console.Error.WriteLine($"STOP_SERVICE_TIMEOUT serviceCleanup={serviceCleanupTask.Status}; pendingAdmissions={_server.PendingCallAdmissionsForDiagnostics}; activeCalls={_server.ActiveCallCountForDiagnostics}");
                            faulted = true;
                            _serviceCleanupObserver = ObserveCleanupFailureAsync(
                                serviceCleanupTask,
                                "Services");
                        }
                    }
                    catch (Exception exception)
                    {
                        faulted = true;
                        SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Services", exception);
                        AddTaskFailures(ref stopFailures, serviceCleanupTask, exception);
                    }
                }
            }
            catch (Exception exception)
            {
                faulted = true;
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Stop", exception);
                (stopFailures ??= []).Add(exception);
            }

            var terminalFailure = CreateTerminalFailure(faulted, stopFailures);
            _server.TransitionTo(terminalFailure is null ? ServerState.Stopped : ServerState.Faulted);
            Volatile.Write(ref _terminalFailure, terminalFailure);
            if (terminalFailure is null)
            {
                _terminalCompletion.TrySetResult(true);
                return;
            }

            _terminalCompletion.TrySetException(terminalFailure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }

        private async Task CleanupAfterRuntimeFailureAsync()
        {
            var timeProvider = _server._runtimeContext.TimeProvider;
            var deadline = SharpLinkTime.AddDuration(
                timeProvider.GetTimestamp(),
                _server._shutdownPlan.CleanupBudget,
                timeProvider.TimestampFrequency);

            CancelForShutdown(_acceptCts, "AcceptCancellation");
            _server._admissionController?.StopAccepting();
            _server.BeginDrainDynamicModules();
            _server._frameworkTasks.Seal();
            CancelForShutdown(_forceStopCts, "CallCancellation");
            TrySignalCallsDrained();
            var callsDrained = _callsDrained.Task.IsCompletedSuccessfully;
            if (!callsDrained)
            {
                var unfinishedCalls = _server._callAdmission.ActiveCallCount;
                if (unfinishedCalls > 0)
                {
                    SharpLinkServer.LogForcedCallsRemaining(_server._logger, unfinishedCalls);
                    SharpLinkTelemetry.RecordForcedStopCalls(unfinishedCalls);
                }

                _deferredServiceCleanupTask ??=
                    DisposeServicesWhenDrainedAsync(_callsDrained.Task);
            }

            var frameworkCleanupTask = Task.WhenAll(
                StartListenerDispose(_server._transportListener),
                DisposeAllSessionsAsync(),
                WaitForAcceptRuntimeShutdownAsync(),
                _server._frameworkTasks.DrainAsync());
            var frameworkCleanupCompleted = false;
            try
            {
                frameworkCleanupCompleted = await WaitUntilWithRuntimeTimeAsync(
                    frameworkCleanupTask,
                    deadline).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                frameworkCleanupCompleted = true;
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Framework", exception);
            }

            if (frameworkCleanupCompleted)
            {
                _acceptCts.Dispose();
                _forceStopCts.Dispose();
            }
            else
            {
                SharpLinkServer.LogFrameworkCleanupTimeout(
                    _server._logger,
                    (int)_server._shutdownPlan.CleanupBudget.TotalSeconds);
                _shutdownCleanupObserver = ObserveShutdownAndDisposeTokensAsync(frameworkCleanupTask);
            }

            if (callsDrained)
            {
                var serviceCleanupTask = DisposeRegisteredServicesAsync();
                try
                {
                    if (!await WaitUntilWithRuntimeTimeAsync(
                            serviceCleanupTask,
                            deadline).ConfigureAwait(false))
                    {
                        _serviceCleanupObserver = ObserveCleanupFailureAsync(
                            serviceCleanupTask,
                            "Services");
                    }
                }
                catch (Exception exception)
                {
                    SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Services", exception);
                }
            }
        }

        internal void TrySignalCallsDrained(ServerConnectionState? releasingConnection = null)
        {
            if (_server.CurrentState is not (ServerState.Draining or ServerState.Stopped or ServerState.Faulted))
                return;

            var pendingAdmissions = _server._callAdmission.PendingCallAdmissions;
            if (pendingAdmissions != 0)
                return;

            var globalActiveCalls = _server._callAdmission.ActiveCallCount;
            if (globalActiveCalls != 0)
                return;

            var releasingConnectionActiveCalls = releasingConnection?.ActiveCalls ?? 0;
            if (releasingConnection is not null && releasingConnectionActiveCalls != 0)
            {
                throw new InvalidOperationException(
                    "Server drain cannot complete before the releasing connection publishes its local call release.");
            }

            if (Interlocked.CompareExchange(ref _callDrainSignalState, 1, 0) != 0)
                return;

            Volatile.Write(ref _lastCallDrainSignalGlobalCalls, globalActiveCalls);
            Volatile.Write(ref _lastCallDrainSignalPendingAdmissions, pendingAdmissions);
            Volatile.Write(ref _lastCallDrainSignalLocalCalls, releasingConnectionActiveCalls);
            Volatile.Write(ref _callDrainSignalState, 2);
            _callsDrained.TrySetResult(true);
        }

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

        internal ServerStopDiagnosticSnapshot? LastStopDiagnostics
            => Volatile.Read(ref _lastStopDiagnostics);

        internal ServerDeferredTaskDiagnosticSnapshot CaptureDeferredTaskSnapshot(int deferredConnectionCleanups)
            => new(
                Volatile.Read(ref _deferredServiceCleanupTask)?.Status,
                Volatile.Read(ref _shutdownCleanupObserver)?.Status,
                Volatile.Read(ref _serviceCleanupObserver)?.Status,
                deferredConnectionCleanups);

        internal void AssertCallAccountingInvariant()
        {
            if (_server._callAdmission.ActiveCallCount < 0)
                throw new InvalidOperationException("Server global active call count became negative.");
            if (_server._callAdmission.PendingCallAdmissions < 0)
                throw new InvalidOperationException("Server pending call admission count became negative.");
            if (_callsDrained.Task.IsCompletedSuccessfully && _server._callAdmission.ActiveCallCount != 0)
            {
                throw new InvalidOperationException(
                    "Server call drain completed before global active calls reached zero.");
            }
        }

        internal void ForceStop()
        {
            try
            {
                _forceStopCts.Cancel();
            }
            catch (ObjectDisposedException) when (_server.CurrentState == ServerState.Stopped)
            {
            }
        }

    }
}
