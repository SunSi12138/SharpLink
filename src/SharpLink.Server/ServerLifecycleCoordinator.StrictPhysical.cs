namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal sealed partial class ServerLifecycleCoordinator
    {
        private const int StrictDrainRequested = 1;
        private const int StrictDrainObserved = 2;
        private const int TerminalObserverClosed = 4;
        private const int StrictTaskRegistered = 8;
        // Existing state gate protects this bounded exceptional-path receipt. The
        // task is the actual single supervisor drain, never a timeout/proxy task.
        private int _strictPhysicalState;
        private Task? _frameworkDrainTask;
        private Exception? _strictFrameworkDrainFailure;

        internal (Task? Drain, bool Requested, bool Observed, Exception? Failure)
            StrictPhysicalDrainForDiagnostics
        {
            get
            {
                lock (_stateGate)
                    return (_frameworkDrainTask,
                        (_strictPhysicalState & StrictDrainRequested) != 0,
                        (_strictPhysicalState & StrictDrainObserved) != 0,
                        _strictFrameworkDrainFailure);
            }
        }

        internal void MarkStrictPhysicalRegistration()
        {
            lock (_stateGate) _strictPhysicalState |= StrictTaskRegistered;
        }

        private bool HasStrictPhysicalOwnership
        {
            get { lock (_stateGate) return (_strictPhysicalState & (StrictDrainRequested | StrictTaskRegistered)) != 0; }
        }

        private bool IsStrictDrainRequested
        {
            get { lock (_stateGate) return (_strictPhysicalState & StrictDrainRequested) != 0; }
        }

        private Task GetOrStartFrameworkDrain()
        {
            lock (_stateGate)
            {
                if (_frameworkDrainTask is not null) return _frameworkDrainTask;
                // Also covers cleanup setup throwing before its usual Seal call.
                // Every original drain site uses this same publication boundary.
                _server._frameworkTasks.Seal();
                return _frameworkDrainTask = _server._frameworkTasks.DrainAsync();
            }
        }

        internal void BeginAndObserveStrictPhysicalFailure(Exception rawFailure, bool retainedBySupervisor = true)
        {
            ArgumentNullException.ThrowIfNull(rawFailure);
            lock (_stateGate)
            {
                _strictPhysicalState |= StrictDrainRequested;
                // A failure first arriving during ordinary Draining still needs its
                // primary retained if that stop exhausts its budget before drain.
                _terminalFailure = retainedBySupervisor
                    ? _terminalFailure ?? rawFailure
                    : CombineStrictLifecycleFailures(_terminalFailure, rawFailure);
            }
            // Preserve the first primary, immediate Faulted escalation, and shared
            // Stop task. An existing unrelated primary must never be relabeled.
            BeginAndObserveTerminalFailure(rawFailure);
        }

        internal void RetainStrictPhysicalReportingFailure(Exception rawFailure, Exception reportingFailure)
        {
            // Recovery performs no diagnostic/user callback. The existing lifecycle
            // roots own both failures even when the first escalation attempt failed.
            lock (_stateGate)
            {
                _strictPhysicalState |= StrictDrainRequested;
                _terminalFailure = CombineStrictLifecycleFailures(
                    _terminalFailure, new AggregateException(rawFailure, reportingFailure));
                _server.TransitionTo(ServerState.Faulted);
                _stopTask ??= CompleteFaultedStopAsync();
                EnsureTerminalObserverLocked(_stopTask);
            }
        }

        private void EnsureTerminalObserverLocked(Task stopTask)
        {
            if (_terminalFailureObserverTask is not null &&
                ((_strictPhysicalState & TerminalObserverClosed) == 0 ||
                 (_strictPhysicalState & (StrictDrainRequested | StrictDrainObserved)) != StrictDrainRequested))
                return;
            _strictPhysicalState &= ~TerminalObserverClosed;
            _terminalFailureObserverTask = ObserveTerminalFailureAsync(stopTask);
        }

        private async Task ObserveTerminalFailureAsync(Task stopTask)
        {
            try { await stopTask.ConfigureAwait(false); }
            catch
            {
                // The original shared stop task remains faulted for all callers.
            }

            Task? drain;
            lock (_stateGate)
            {
                if ((_strictPhysicalState & StrictDrainRequested) == 0 ||
                    (_strictPhysicalState & StrictDrainObserved) != 0)
                {
                    // This bit, not Task.IsCompleted, closes the late-callback gap.
                    _strictPhysicalState |= TerminalObserverClosed;
                    return;
                }
                drain = GetOrStartFrameworkDrain();
            }
            Exception? failure = null;
            try { await drain.ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
            lock (_stateGate)
            {
                _strictFrameworkDrainFailure = failure;
                _strictPhysicalState |= StrictDrainObserved | TerminalObserverClosed;
            }
            if (failure is not null)
            {
                try { SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "StrictPhysicalDrain", failure); }
                catch (Exception reportingFailure) { RetainDeferredStrictCleanupFailure(reportingFailure); }
            }
        }

        private Exception? CaptureStrictDrainOutcomeForStop()
        {
            if (!IsStrictDrainRequested) return null;
            var drain = GetOrStartFrameworkDrain();
            if (!drain.IsCompleted)
                return new TimeoutException(
                    "Strict physical supervisor drain remains incomplete; its exact task and eventual outcome remain owned by the lifecycle observer.");

            Exception? failure = null;
            try { drain.GetAwaiter().GetResult(); }
            catch (Exception exception) { failure = exception; }
            lock (_stateGate)
            {
                _strictFrameworkDrainFailure = failure;
                _strictPhysicalState |= StrictDrainObserved;
            }
            return failure;
        }

        internal Task? StrictShutdownCleanupObserverForDiagnostics
            => Volatile.Read(ref _shutdownCleanupObserver);

        internal Exception? StrictPhysicalTerminalFailureForDiagnostics
        {
            get { lock (_stateGate) return _terminalFailure; }
        }

        private void LogStrictStopFailure(ref List<Exception>? failures, string stage, Exception failure)
        {
            try { SharpLinkServer.LogDeferredCleanupFailed(_server._logger, stage, failure); }
            catch (Exception reportingFailure) { (failures ??= []).Add(reportingFailure); }
        }

        private void RetainStrictCleanupFailure(ref Exception? retained, string stage, Exception failure)
        {
            retained = CombineStrictLifecycleFailures(retained, failure);
            try { SharpLinkServer.LogDeferredCleanupFailed(_server._logger, stage, failure); }
            catch (Exception diagnosticFailure)
            {
                retained = CombineStrictLifecycleFailures(retained, diagnosticFailure);
            }
        }

        private void DisposeStrictShutdownTokens(ref Exception? failure)
        {
            try { _acceptCts.Dispose(); }
            catch (Exception exception) { failure = CombineStrictLifecycleFailures(failure, exception); }
            try { _forceStopCts.Dispose(); }
            catch (Exception exception) { failure = CombineStrictLifecycleFailures(failure, exception); }
        }

        private void RetainDeferredStrictCleanupFailure(Exception failure)
        {
            lock (_stateGate)
                _terminalFailure = CombineStrictLifecycleFailures(_terminalFailure, failure);
        }

        private static Exception? CombineStrictLifecycleFailures(Exception? first, Exception? next)
            => first is null ? next : next is null ? first : new AggregateException(first, next);
    }
}
