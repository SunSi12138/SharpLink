namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Owns the one-shot server lifecycle state machine. The outer server supplies composed
    /// dependencies and request/connection operations; this coordinator owns stop idempotency,
    /// drain publication, shutdown cancellation, bounded framework teardown, and final cleanup order.
    ///
    /// Invariants:
    /// - exactly one run task and one shared stop/cleanup task are established;
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
        private Task? _runTask;
        private Task? _stopTask;
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

        internal SharpLinkHealthStatus HealthStatus => _server.CurrentState switch
        {
            ServerState.Running => SharpLinkHealthStatus.Ready,
            ServerState.Draining => SharpLinkHealthStatus.Draining,
            _ => SharpLinkHealthStatus.Unhealthy
        };

        internal ValueTask RunAsync(CancellationToken cancellationToken)
        {
            Task runTask;
            lock (_stateGate)
            {
                if (_runTask is null)
                {
                    if (_server.CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
                    {
                        return ValueTask.FromException(new SharpLinkException(
                            SharpLinkErrorCode.ConnectionClosed,
                            "Server cannot be restarted."));
                    }

                    _runTask = RunCoreAsync(cancellationToken);
                }

                runTask = _runTask;
            }

            return new ValueTask(runTask);
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

        private async Task RunCoreAsync(CancellationToken cancellationToken)
        {
            _server.TransitionTo(ServerState.Starting);
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _acceptCts.Token);
            var acceptToken = runCts.Token;
            _server.TransitionTo(ServerState.Running);

            try
            {
                await _server.RunAcceptLoopAsync(acceptToken).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested && _server.CurrentState == ServerState.Running)
                {
                    await GetOrCreateStopTask(TimeSpan.Zero).ConfigureAwait(false);
                }
                else
                {
                    Task? stopTask;
                    lock (_stateGate)
                        stopTask = _stopTask;
                    if (stopTask is not null)
                        await stopTask.ConfigureAwait(false);
                }
            }
            catch
            {
                Task cleanupTask;
                lock (_stateGate)
                {
                    _server.TransitionTo(ServerState.Faulted);
                    _stopTask ??= CleanupAfterRunFailureAsync();
                    cleanupTask = _stopTask;
                }

                await cleanupTask.ConfigureAwait(false);
                throw;
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

            _server.TransitionTo(faulted ? ServerState.Faulted : ServerState.Stopped);
            ThrowStopFailures(stopFailures);
        }

        private async Task CleanupAfterRunFailureAsync()
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

        private async Task SendGoAwayToAllAsync()
        {
            var connections = _server._connectionRegistry.SnapshotActive();
            var tasks = new Task[connections.Length];
            for (var index = 0; index < connections.Length; index++)
            {
                var connection = connections[index];
                connection.MarkDraining();
                tasks[index] = SendGoAwayAsync(connection);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
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
            var connections = _server._connectionRegistry.SnapshotActive();
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

        private async Task DisposeAllSessionsAsync()
        {
            var connections = _server._connectionRegistry.SnapshotActive();
            var tasks = new Task[connections.Length];
            for (var index = 0; index < connections.Length; index++)
                tasks[index] = _server.DisconnectConnectionAsync(connections[index]).AsTask();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                ThrowUnexpectedShutdownTaskFailures(tasks);
            }
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
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Services", exception);
            }
        }

        private async Task DisposeRegisteredServicesAsync()
        {
            List<Exception>? failures = null;
            try
            {
                await _server.ReleaseDrainedDynamicModulesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await _server._serviceCleanup.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (_server._admissionController is not null)
            {
                try
                {
                    await _server._admissionController.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                _server._runtimeContext.Dispose();
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

        private Task<bool> WaitUntilWithRuntimeTimeAsync(Task task, long deadline)
            => WaitUntilWithProviderAsync(task, deadline, _server._runtimeContext.TimeProvider);

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
            return await SharpLinkTimer.WaitAsync(task, remaining, timeProvider).ConfigureAwait(false);
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

        private void CancelForShutdown(CancellationTokenSource cancellation, string cleanupName)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, cleanupName, exception);
            }
        }

        private async Task ObserveShutdownAndDisposeTokensAsync(Task shutdownTask)
        {
            try
            {
                await shutdownTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Framework", exception);
            }
            finally
            {
                _acceptCts.Dispose();
                _forceStopCts.Dispose();
            }
        }

        private async Task ObserveCleanupFailureAsync(Task cleanupTask, string cleanupName)
        {
            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, cleanupName, exception);
            }
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

        private static void ThrowUnexpectedShutdownTaskFailures(Task[] tasks)
        {
            List<Exception>? unexpected = null;
            for (var taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                if (tasks[taskIndex].Exception is not { } aggregate)
                    continue;
                foreach (var exception in aggregate.Flatten().InnerExceptions)
                {
                    if (SharpLinkServer.IsExpectedSessionShutdownException(exception))
                        continue;
                    (unexpected ??= []).Add(exception);
                }
            }

            if (unexpected is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
            if (unexpected is not null)
                throw new AggregateException(unexpected);
        }
    }
}
