namespace SharpLink.Server;

internal sealed partial class ServerServiceModuleRegistry
{
    private int _preparationWriters;
    private bool _retirementFailed;

    // Caller holds Gate. These owners cover only cold construction/rollback/retirement callbacks.
    internal IDisposable EnterPreparation()
    {
        _preparationWriters++;
        return new PreparationLease(this);
    }

    internal bool HasRetirementFailure => _retirementFailed;

    internal void MarkRetirementFailed() => _retirementFailed = true;

    internal void ValidateMaintenanceConfiguration()
    {
        if (_coldWriter is not null || _preparationWriters != 0 || _retirementFailed ||
            _unregisterOperations.Count != 0 || _detachedModuleServices.Count != 0 ||
            _dynamicModules.Values.Any(static module => module.State != SharpLinkDynamicModuleState.Running))
            throw new InvalidOperationException("Server catalog ownership is not stable for maintenance.");
    }

    private sealed class PreparationLease(ServerServiceModuleRegistry owner) : IDisposable
    {
        private ServerServiceModuleRegistry? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner.Gate) owner._preparationWriters--;
        }
    }
}
