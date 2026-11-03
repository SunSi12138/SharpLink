namespace SharpLink.Client;

// Fixed cold inventory custody, never consulted by RPC. Original preparation
// ownership is detached before exposure; clone leases are temporary, not refunds.
internal sealed class CoordinatorAssemblyCapacity(SharpLinkMultiClusterClient owner) : IDisposable
{
    internal const int Slots = 96;
    private ColdMetadataReservation? _token;
    private IDisposable? _lease;
    private CoordinatorAssemblyCapacity? _sibling;
    private bool _detached;
    private bool _attempted;
    private bool _leaseReturned;
    internal bool Refunded { get; private set; }
    internal bool CanClaim => _detached && !_attempted && _sibling is null;
    internal Exception? Failure { get; private set; }
    internal CoordinatorAssemblyCapacity? FailedNext;
    internal void PreserveAdmissionFailure(Exception failure) => Failure = failure;

    internal void Initialize(ColdMetadataPreparation preparation)
    {
        _token = preparation.ReserveCollection(Slots);
        _lease = _token.Retain();
        owner.AssemblyRetirementForDiagnostics?.Invoke("BeforeOriginalDetach", this);
        _token.Dispose();
        _detached = _token.IsDetached && _token.OriginalReleaseSucceeded;
        if (!_detached) throw new InvalidOperationException("Assembly inventory did not detach its original reference.");
    }
    internal CoordinatorAssemblyCapacity RetainForClone()
    {
        if (!CanClaim) throw new InvalidOperationException("An active or uncertain inventory cannot be cloned.");
        var clone = new CoordinatorAssemblyCapacity(owner) { _token = _token, _detached = true };
        owner.AssemblyRetirementForDiagnostics?.Invoke("BeforeCloneLeaseRetain", this);
        clone._lease = _token!.Retain();
        clone._sibling = this;
        _sibling = clone;
        try
        {
            owner.AssemblyRetirementForDiagnostics?.Invoke("AfterCloneLeaseRetain", clone);
            return clone;
        }
        catch (Exception primary)
        {
            try { clone.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
    }
    public void Dispose()
    {
        if (_attempted)
        {
            if (Failure is { } prior) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(prior).Throw();
            return;
        }
        _attempted = true;
        try
        {
            if (!_detached)
            {
                // Initial construction failed before exposure. Both real owners
                // still have to be discharged; never infer detach from a lease.
                if (_token is { OriginalReleaseSucceeded: false, IsDetached: true })
                    throw new InvalidOperationException("The original inventory release has an uncertified outcome.");
                if (_token is { OriginalReleaseSucceeded: false }) _token.Dispose();
                _detached = _token is null || (_token.IsDetached && _token.OriginalReleaseSucceeded);
            }
            owner.AssemblyRetirementForDiagnostics?.Invoke("BeforeFinalLeaseRelease", this);
            _lease?.Dispose();
            _leaseReturned = true;
            _lease = null;
            if (_sibling is { } sibling)
            {
                if (!sibling._attempted)
                {
                    // The survivor now has the sole independent lease. This
                    // completed nonfinal decrement is NOT a refund certificate.
                    sibling._sibling = null;
                    _sibling = null;
                }
                else if (sibling._leaseReturned)
                {
                    Refunded = _detached && _token!.IsRefunded;
                    _sibling = null;
                }
            }
            else
            {
                Refunded = _detached && (_token is null || _token.IsRefunded);
                if (!Refunded) throw new InvalidOperationException("Sole assembly capacity did not refund.");
            }
            if (_sibling is { Failure: { } uncertain })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(uncertain).Throw();
            if (Refunded)
            {
                Refunded = false;
                owner.RefundAssemblyInventory();
                Refunded = true;
            }
        }
        catch (Exception failure) { Failure = failure; throw; }
    }
}
