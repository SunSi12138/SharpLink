namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly Lock _bindingMaintenanceGate = new();
    private Task? _bindingMaintenanceTask;
    private CancellationTokenSource? _bindingMaintenanceCancellation;
    private Task? _bindingMaintenanceStopTask;
    private bool _bindingMaintenanceStopping;
    private const int BindingMaintenanceScanLimit = 256;
    internal Action? BindingMaintenanceDelayScheduledForTesting { get; set; }
    internal Action<int>? BindingMaintenanceTickCompletedForTesting { get; set; }

    internal void NotifyBindingControlOwnerReleased(ClientConnection connection)
        => RetireDrainingConnectionIfIdle(connection);

    private void ObserveBindingMaintenanceTask(Task task, string operation)
        => TrackFrameworkTask(task, operation);

    internal async Task TrackBindingCandidateAsync(ContractCallSite site, ContractCallSite.BoundTarget target)
    {
        BindingBudget.SetTaskObserver(ObserveBindingMaintenanceTask);
        await target.Connection.Binding.TrackCandidateAsync(site, target).ConfigureAwait(false);
        EnsureBindingMaintenanceStarted();
    }

    private Task RetireBindingCandidateAsync(ContractCallSite.BoundTarget target)
        => target.Connection.Binding.RetireCandidateAsync(target);

    private void EnsureBindingMaintenanceStarted()
    {
        Task? started = null;
        lock (_bindingMaintenanceGate)
        {
            if (_bindingMaintenanceStopping || BindingBudget.TrackingSealed ||
                _bindingMaintenanceTask is not null || BindingBudget.CandidateRecords == 0)
                return;
            var cancellation = new CancellationTokenSource();
            _bindingMaintenanceCancellation = cancellation;
            started = _bindingMaintenanceTask = RunBindingMaintenanceAsync(cancellation);
        }
        TrackFrameworkTask(started, "BindingMaintenance");
    }

    private async Task RunBindingMaintenanceAsync(CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            while (BindingBudget.CandidateRecords != 0)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(1), _runtimeContext.TimeProvider, cancellation.Token);
                BindingMaintenanceDelayScheduledForTesting?.Invoke();
                await delay.ConfigureAwait(false);
                var examined = ScanBindingCandidates(cancellation.Token);
                BindingMaintenanceTickCompletedForTesting?.Invoke(examined);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // A failed maintenance infrastructure owner cannot silently abandon tracking.
            // Seal new tracking and terminalize the bounded set; root Stop joins physical owners.
            var failures = new List<Exception> { exception };
            foreach (var session in BindingBudget.SealAndCaptureSessions())
            {
                try { session.Close(exception); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            ThrowStopCleanupFailures(failures);
            throw;
        }
        finally
        {
            // No timer/scan work survives this point. Publish idle and restart under the same
            // gate as new tracking, so a candidate arriving during epoch exit cannot lose its wakeup.
            try { cancellation.Cancel(); }
            finally
            {
                lock (_bindingMaintenanceGate)
                {
                    cancellation.Dispose();
                    _bindingMaintenanceCancellation = null;
                    _bindingMaintenanceTask = null;
                }
                EnsureBindingMaintenanceStarted();
            }
        }
    }

    private int ScanBindingCandidates(CancellationToken cancellationToken)
    {
        var examined = 0;
        for (; examined < BindingMaintenanceScanLimit; examined++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BindingBudget.CandidateRecords == 0 ||
                !BindingBudget.TryTakeScanSlot(out var session, out var slot)) break;
            // One slot, not one whole Session; includes locally Draining Sessions.
            // Cleanup is bounded/supervised separately, never serially awaited by the scanner.
            session!.ExamineCandidate(slot);
        }
        return examined;
    }

    internal Task StopBindingMaintenanceAsync()
    {
        lock (_bindingMaintenanceGate)
        {
            if (_bindingMaintenanceStopTask is not null) return _bindingMaintenanceStopTask;
            _bindingMaintenanceStopping = true;
            var sessions = BindingBudget.SealAndCaptureSessions();
            // Starts with Yield; no Session/transport callback or await under this/root Stop gate.
            return _bindingMaintenanceStopTask = StopBindingMaintenanceCoreAsync(sessions);
        }
    }

    private async Task StopBindingMaintenanceCoreAsync(ClientBindingSession[] sessions)
    {
        await Task.Yield();
        Task? maintenance;
        CancellationTokenSource? cancellation;
        lock (_bindingMaintenanceGate)
        {
            maintenance = _bindingMaintenanceTask;
            cancellation = _bindingMaintenanceCancellation;
        }
        var failures = new List<Exception>();
        try { if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { } // The captured epoch already exited and disposed its source.
        catch (Exception exception) { failures.Add(exception); }
        if (maintenance is not null)
        {
            try { await maintenance.ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        var stopped = CreateConnectionClosedException("Client binding tracking is stopping.");
        // Close all exact Sessions before waiting for any owner. Never join Session.Dispose,
        // which itself may depend on these Bind/cleanup/framework tasks.
        foreach (var session in sessions)
        {
            try { session.Close(stopped); }
            catch (Exception exception) { failures.Add(exception); }
        }
        for (var index = 0; index < sessions.Length; index++)
        {
            try { await sessions[index].TrackingDrained.ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            sessions[index] = null!;
        }
        ThrowStopCleanupFailures(failures);
    }
}
