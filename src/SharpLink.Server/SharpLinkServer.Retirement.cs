using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private void RequestDynamicRetirementCancellation()
    {
        lock (_registryGate)
            foreach (var module in _dynamicModules.Values) module.RetirementControl?.RequestForce();
    }

    // Caller holds the existing registry gate. All allocation/capacity work is
    // before the old admission cut. No callback starts until Activate outside it.
    private RetirementControl CreateRetirementLocked(Assembly assembly, SharpLinkDynamicModule module,
        RetirementOrigin origin, RetirementGraceBudget? budget, Func<bool, Exception?>? actions)
    {
        if (_unregisterOperations.ContainsKey(assembly) || module.RetirementControl is not null)
            throw new InvalidOperationException("The exact module already has a retirement stage.");
        _unregisterOperations.EnsureCapacity(checked(_unregisterOperations.Count + 1));
        var control = new RetirementControl(module, _runtimeContext.Domain, origin, budget, actions);
        Task<SharpLinkAssemblyUnregisterResult> operation;
        Task<(Exception? Failure, bool SupervisorOwned)> observation;
        if (ExecutionContext.IsFlowSuppressed())
        {
            operation = RetireControlledModuleAsync(assembly, module, control);
            observation = ObserveControlledRetirementAsync(assembly, module, control, operation);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                operation = RetireControlledModuleAsync(assembly, module, control);
                observation = ObserveControlledRetirementAsync(assembly, module, control, operation);
            }
        }
        module.RetirementControl = control;
        module.RetirementRegistration = null;
        module.RetirementOperation = operation;
        module.RetirementTracking = control.Tracking.Task;
        module.RetirementObservation = observation;
        // Reference comparer, exact key preflight and pre-sized capacity: no user
        // callback or ordinary fallible preparation remains in this root transfer.
        _unregisterOperations.Add(assembly, (operation, module));
        return control;
    }

    private async Task<SharpLinkAssemblyUnregisterResult> RetireControlledModuleAsync(
        Assembly assembly, SharpLinkDynamicModule module, RetirementControl control)
    {
        var committed = await control.StartDecision.ConfigureAwait(false);
        Exception? failure = null;
        Exception? trackingFailure = null;
        try
        {
            if (committed && control.Origin != RetirementOrigin.Stop)
            {
                FrameworkTaskRegistration? receipt = null;
                try
                {
                    TrackOwnedRetirementTask(module.RetirementOperation!, module, ref receipt);
                }
                catch (Exception exception)
                {
                    trackingFailure = exception;
                    AddRetirementFailure(ref failure, exception);
                }
                finally { module.RetirementRegistration = receipt; }
            }
            control.Tracking.TrySetResult(trackingFailure);
            try
            {
                using (RetirementCallbackScope.Enter(_runtimeContext.Domain))
                    if (control.RunActions(committed) is { } actionFailure)
                        AddRetirementFailure(ref failure, actionFailure);
            }
            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
            finally { control.Ready.TrySetResult(failure); }

            // Only this original physical owner consumes driver outcome data.
            // Neither waiter nor Stop aggregates its failures a second time.
            var cancellation = await control.Driver.Task.ConfigureAwait(false);
            cancellation.AppendFailures(ref failure);
            if (committed)
            {
                if (failure is null && cancellation.CancellationAttempted && module.RemainingCalls != 0)
                {
                    module.MarkDrainTimedOut();
                    control.GraceDecision.TrySetResult(new SharpLinkAssemblyUnregisterResult
                    {
                        ReferencesReleased = false,
                        RemainingCalls = module.RemainingCalls,
                        RemainingStreams = module.RemainingStreams
                    });
                }
                else control.GraceDecision.TrySetResult(null);

                await module.WaitForDrainAsync().ConfigureAwait(false);
                // A faulted timer-disposal task is not proof of timer exit. Its
                // handle stays rooted on the driver; keep module/storage and the
                // exact operation discoverable, and make Stop fail closed rather
                // than advance dependent cleanup or refund the owner reservation.
                if (!cancellation.HasRetainedTimer)
                    await ReleaseModuleAsync(assembly, module).ConfigureAwait(false);
            }
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, PhysicalTaskFailureException.GetRaw(exception)); }
        finally
        {
            // These are real worker-owned phase completions, not proof of a
            // callback exit when a callback is still blocked inside RunActions.
            control.Tracking.TrySetResult(trackingFailure);
            control.Ready.TrySetResult(failure);
            control.GraceDecision.TrySetResult(null);
        }
        if (failure is not null) throw PhysicalTaskFailureException.From(failure);
        return new SharpLinkAssemblyUnregisterResult { ReferencesReleased = committed };
    }

    private async Task<(Exception? Failure, bool SupervisorOwned)> ObserveControlledRetirementAsync(
        Assembly assembly, SharpLinkDynamicModule module, RetirementControl control,
        Task<SharpLinkAssemblyUnregisterResult> operation)
    {
        var committed = await control.StartDecision.ConfigureAwait(false);
        // Abort also joins the actual physical worker (and hence its driver).
        try { await operation.ConfigureAwait(false); }
        catch { /* Raw source outcome retains its public/strict/Stop owner. */ }
        Exception? failure = null;
        var receipt = committed ? module.RetirementRegistration : null;
        try
        {
            using (RetirementCallbackScope.Enter(_runtimeContext.Domain))
                if (receipt is not null) receipt.Supervisor.CompleteAcceptedRegistration(receipt);
        }
        catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        if (!committed || module.State == SharpLinkDynamicModuleState.Released)
        {
            try { control.ReleaseReservation(); }
            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        }

        // Refund failures participate BEFORE removal, retaining discoverability.
        lock (_registryGate)
        {
            if (_unregisterOperations.TryGetValue(assembly, out var current) &&
                ReferenceEquals(current.Task, operation) && ReferenceEquals(current.Module, module))
            {
                if (!committed && failure is null && operation.IsCompletedSuccessfully)
                {
                    _unregisterOperations.Remove(assembly);
                    module.RetirementControl = null;
                    module.RetirementOperation = null;
                    module.RetirementTracking = null;
                    module.RetirementObservation = null;
                }
                else if (committed && receipt is not null && failure is null &&
                    module.State == SharpLinkDynamicModuleState.Released)
                    _unregisterOperations.Remove(assembly);
            }
        }
        return (failure, receipt is not null);
    }

    private static async ValueTask<SharpLinkAssemblyUnregisterResult> WaitForControlledRetirementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, RetirementControl control,
        Task<(Exception? Failure, bool SupervisorOwned)> observation, CancellationToken cancellationToken)
    {
        await RetirementWaiter.WaitForCompletionAsync(control.GraceDecision.Task, cancellationToken).ConfigureAwait(false);
        var deferred = await control.GraceDecision.Task.ConfigureAwait(false);
        // A cached early drain decision cannot hide a later terminal source
        // failure, or bypass physical cleanup after the last call has exited.
        if (deferred is not null && !operation.IsCompleted && control.Module.RemainingCalls != 0)
            return deferred.Value;

        // Waiter cancellation is outside the catch that consumes ORIGINAL source
        // outcome. An original OCE never gets mistaken for this wait's token.
        await RetirementWaiter.WaitForCompletionAsync(operation, cancellationToken).ConfigureAwait(false);
        Exception? failure = null;
        SharpLinkAssemblyUnregisterResult result = default!;
        try { result = await operation.ConfigureAwait(false); }
        catch (Exception exception) { failure = PhysicalTaskFailureException.GetRaw(exception); }
        await RetirementWaiter.WaitForCompletionAsync(observation, cancellationToken).ConfigureAwait(false);
        var observed = await observation.ConfigureAwait(false);
        if (observed.Failure is not null) AddRetirementFailure(ref failure, observed.Failure);
        ThrowRetirementFailure(failure);
        return result;
    }

    private async ValueTask<SharpLinkAssemblyUnregisterResult> RetryUnregisterAfterStageAsync(
        Assembly assembly, RetirementGraceBudget budget,
        Task<SharpLinkAssemblyUnregisterResult> operation,
        Task<(Exception? Failure, bool SupervisorOwned)> observation, CancellationToken cancellationToken)
    {
        await RetirementWaiter.WaitForCompletionAsync(observation, cancellationToken).ConfigureAwait(false);
        var observed = await observation.ConfigureAwait(false);
        Exception? failure = observed.Failure;
        try { await operation.ConfigureAwait(false); }
        catch (Exception exception) { AddRetirementFailure(ref failure, PhysicalTaskFailureException.GetRaw(exception)); }
        ThrowRetirementFailure(failure);
        return await UnregisterAssemblyCore(assembly, budget, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SharpLinkAssemblyReplacementResult> WaitForControlledReplacementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, RetirementControl control,
        Task<(Exception? Failure, bool SupervisorOwned)> observation, CancellationToken cancellationToken)
        => SharpLinkAssemblyReplacementResult.Published(
            await WaitForControlledRetirementAsync(operation, control, observation, cancellationToken).ConfigureAwait(false));

    private static async ValueTask<SharpLinkAssemblyReplacementResult> WaitForRejectedReplacementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation, RetirementControl control,
        Task<(Exception? Failure, bool SupervisorOwned)> observation,
        SharpLinkAssemblyReplacementResult rejection, CancellationToken cancellationToken)
    {
        await WaitForControlledRetirementAsync(operation, control, observation, cancellationToken).ConfigureAwait(false);
        return rejection;
    }

    private async Task<(Task<SharpLinkAssemblyUnregisterResult> Operation,
        Task<(Exception? Failure, bool SupervisorOwned)> Observation)> GetShutdownRetirementAsync(
        Assembly assembly, SharpLinkDynamicModule module)
    {
        while (true)
        {
            RetirementControl? selected = null;
            RetirementControl? control = null;
            Task<SharpLinkAssemblyUnregisterResult>? operation = null;
            Task<(Exception? Failure, bool SupervisorOwned)>? observation = null;
            var abortedStage = false;
            try
            {
                lock (_registryGate)
                {
                    if (_unregisterOperations.TryGetValue(assembly, out var existing))
                    {
                        if (!ReferenceEquals(existing.Module, module))
                            throw new InvalidOperationException("Shutdown retirement selected another module generation.");
                        operation = existing.Task;
                        control = module.RetirementControl;
                        abortedStage = control is { IsCommitted: false };
                    }
                    else if (module.State == SharpLinkDynamicModuleState.Released &&
                        module.RetirementOperation is { } completed)
                    {
                        operation = completed;
                        control = module.RetirementControl;
                    }
                    else
                    {
                        if (!_dynamicModules.TryGetValue(assembly, out var current) || !ReferenceEquals(current, module))
                            throw new InvalidOperationException("Shutdown cannot elect an unregistered module generation.");
                        selected = CreateRetirementLocked(assembly, module, RetirementOrigin.Stop, null, null);
                        module.TryBeginDraining();
                        selected.MarkCommitted();
                        control = selected;
                        operation = module.RetirementOperation!;
                    }
                    observation = module.RetirementObservation!;
                }
            }
            finally { selected?.Activate(); }
            if (abortedStage)
            {
                var observed = await observation!.ConfigureAwait(false);
                Exception? failure = observed.Failure;
                try { await operation!.ConfigureAwait(false); }
                catch (Exception exception) { AddRetirementFailure(ref failure, PhysicalTaskFailureException.GetRaw(exception)); }
                ThrowRetirementFailure(failure);
                // The abort observer actually joined physical/driver before it
                // cleared the staging slot. Stop now performs its own election.
                continue;
            }
            control!.RequestForce();
            return (operation!, observation!);
        }
    }
}
