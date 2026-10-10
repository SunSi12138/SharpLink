namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    public RpcRequestOperation<T> Rent<T>(
        IRpcCodec<T> responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver = null,
        bool hasResponsePayload = true,
        bool responseNullable = false)
    {
        var ignoredPublication = false;
        return Rent(
            responseCodec, kind, deadline, cancellationToken, out id, completionObserver,
            hasResponsePayload, responseNullable, ref ignoredPublication);
    }

    // Caller-visible publication is set at the successful CAS, before OnRegistered can throw.
    internal RpcRequestOperation<T> Rent<T>(
        IRpcCodec<T> responseCodec,
        PendingCallKind kind,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver,
        bool hasResponsePayload,
        bool responseNullable,
        ref bool registrationPublished)
    {
        ArgumentNullException.ThrowIfNull(responseCodec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRent(
                responseCodec, kind, deadline, cancellationToken, hasResponsePayload, responseNullable,
                completionObserver, out id, out var operation, ref registrationPublished))
            return operation;

        throw CreateResourceExhaustedException();
    }
}
