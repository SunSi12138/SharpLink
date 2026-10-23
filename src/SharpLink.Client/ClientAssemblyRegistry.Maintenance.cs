namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    private int _preparationWriters;
    private bool _retirementFailed;

    // A cold writer owns candidate callbacks and rollback disposal, not only its final map swap.
    // Opt-in rejects an in-flight writer rather than waiting while holding registry/state locks.
    private PreparationLease? TryEnterPreparation()
    {
        lock (_gate)
        {
            if (RejectMutationLocked(out _)) return null;
            return EnterPreparationLocked();
        }
    }

    private PreparationLease EnterPreparationLocked()
    {
        _preparationWriters++;
        return new PreparationLease(this);
    }

    private sealed class PreparationLease(ClientAssemblyRegistry owner) : IDisposable
    {
        private ClientAssemblyRegistry? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._gate) owner._preparationWriters--;
        }
    }
}
