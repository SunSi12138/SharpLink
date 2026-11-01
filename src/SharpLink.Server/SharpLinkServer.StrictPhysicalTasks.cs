namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Only generated physical owners opt into this policy. Legacy runtime registrations
    // retain their existing shutdown classification and lifecycle behavior.
    internal void TrackStrictPhysicalRuntimeTask(
        Task task, string operation, ref FrameworkTaskRegistration? receipt)
    {
        // The supervisor completion can wake Stop before our lifecycle continuation.
        // Guard selected cleanup before publishing that completion race.
        _lifecycle.MarkStrictPhysicalRegistration();
        var registration = _frameworkTasks.TrackStrictPhysical(task, operation, ref receipt);
        if (!ReferenceEquals(registration.Task, task) ||
            !ReferenceEquals(registration.Supervisor, _frameworkTasks) || !registration.IsStrictPhysical)
            throw new InvalidOperationException("Strict physical registration identity changed.");
        // The accepted receipt is already visible if continuation attachment throws.
        // The originating physical owner must then join this exact task and complete
        // that exact registration; it must not report the task's fault a second time.
        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                var (server, accepted) = ((SharpLinkServer, FrameworkTaskRegistration))state!;
                var failure = GetStrictPhysicalTaskFailure(completedTask);
                if (failure is null || server.CurrentState == ServerState.Created)
                    return;
                try { server._lifecycle.BeginAndObserveStrictPhysicalFailure(failure); }
                catch (Exception reportingFailure)
                {
                    // Reporting is not assumed nonthrowing. Retain both raw stages
                    // on the existing lifecycle owner, not on a discarded callback Task.
                    server._lifecycle.RetainStrictPhysicalReportingFailure(failure, reportingFailure);
                }
            },
            (this, registration),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void TrackOwnedRetirementTask(Task task, SharpLinkDynamicModule module,
        ref FrameworkTaskRegistration? receipt)
    {
        _lifecycle.MarkStrictPhysicalRegistration();
        _frameworkTasks.TrackOwnedStrictPhysicalTail(task, "DynamicAssemblyPhysicalRetirement", module, ref receipt);
    }

    internal void ReportStrictPhysicalRuntimeFailure(
        Exception rawFailure, FrameworkTaskRegistration? acceptedReceipt)
    {
        ArgumentNullException.ThrowIfNull(rawFailure);
        if (acceptedReceipt is not null &&
            (!ReferenceEquals(acceptedReceipt.Supervisor, _frameworkTasks) || !acceptedReceipt.IsStrictPhysical))
            throw new InvalidOperationException("The strict physical failure belongs to another supervisor.");
        // This is the caller-owned setup residual, not a second copy of an accepted
        // task's outcome. Keep it as the primary even when it is absent from drain.
        var raw = PhysicalTaskFailureException.GetRaw(rawFailure);
        try { _lifecycle.BeginAndObserveStrictPhysicalFailure(raw, retainedBySupervisor: false); }
        catch (Exception reportingFailure)
        {
            // Direct setup-residual reporting has the same durable fallback as the
            // registered-task callback. An invocation's local aggregate is not its owner.
            _lifecycle.RetainStrictPhysicalReportingFailure(raw, reportingFailure);
        }
    }

    private static Exception? GetStrictPhysicalTaskFailure(Task task)
    {
        if (task.IsCompletedSuccessfully) return null;
        if (task.Exception is { } container)
        {
            if (container.InnerExceptions.Count == 1)
                return UnwrapPhysicalTransport(container.InnerExceptions[0]);
            var failures = new Exception[container.InnerExceptions.Count];
            for (var index = 0; index < failures.Length; index++)
                failures[index] = UnwrapPhysicalTransport(container.InnerExceptions[index]);
            return new AggregateException(failures);
        }
        // An ordinary physical OCE is carried by a non-OCE envelope BEFORE the
        // async method returns. Cancellation here is a broken transport contract,
        // never evidence that we recovered the original exception identity.
        return new InvalidOperationException("A strict physical task canceled without its raw failure envelope.");
    }

    private static Exception UnwrapPhysicalTransport(Exception exception)
        => exception is PhysicalTaskFailureException transport ? transport.RawException : exception;
}
