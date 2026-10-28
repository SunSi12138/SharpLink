namespace SharpLink.Server;

internal sealed partial class AdmissionStateKernel
{
    private static class CleanupFailures
    {
        internal static System.Runtime.CompilerServices.ConditionalWeakTable<AdmissionStateKernel, object>? Owners;
    }

    private bool HasUnpublishedCleanupFailure
        => Volatile.Read(ref CleanupFailures.Owners)?.TryGetValue(this, out _) == true;

    internal void ReleaseUnpublishedBindings(SharpLinkAdmissionController controller)
    {
        List<IDisposable>? dispose = null;
        try
        {
            lock (_registryGate)
                ReleaseBindingsLocked(controller, ref dispose);
            DisposeStates(dispose);
        }
        catch
        {
            // There is no AdmissionProgram to retain when candidate construction never reached
            // registration. Preserve failure without rooting its exception, controller or codec.
            LazyInitializer.EnsureInitialized(ref CleanupFailures.Owners).GetValue(this, static _ => new object());
            throw;
        }
    }

    // The server registry/configuration writer gate is held by the caller. Waiting-capable
    // publications may remain physically owned after a no-waiting replacement is published.
    internal void ValidateMaintenanceEligibility()
    {
        lock (_registryGate)
        {
            if (HasUnpublishedCleanupFailure)
                throw new InvalidOperationException("Unpublished admission cleanup failed; maintenance cannot prove physical quiescence.");
            if (_programs.Any(static program => program.Controller.WaitingEnabled) ||
                _retiredPrograms.Any(static program => !program.IsReclaimed && program.Controller.WaitingEnabled))
                throw new InvalidOperationException(
                    "Queued admission is not supported by cooperative maintenance. Disable waiting and finish all retained admission-generation cleanup before opting in.");
        }
    }
}
