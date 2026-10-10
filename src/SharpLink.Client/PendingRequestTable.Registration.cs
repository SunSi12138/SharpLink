namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    private bool TryRegister<T>(
        PendingCallKind kind,
        RpcRequestOperation<T> operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IRpcCodec<T> responseCodec,
        IPendingCallCompletionObserver? completionObserver,
        bool hasResponsePayload,
        bool responseNullable)
    {
        var ignoredPublication = false;
        return TryRegister(
            kind, operation, dispatcher, deadline, cancellationToken, out id, responseCodec,
            completionObserver, hasResponsePayload, responseNullable, ref ignoredPublication);
    }

    private bool TryRegister<T>(
        PendingCallKind kind,
        RpcRequestOperation<T> operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IRpcCodec<T> responseCodec,
        IPendingCallCompletionObserver? completionObserver,
        bool hasResponsePayload,
        bool responseNullable,
        ref bool registrationPublished)
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            var slots = GetOrCreateSlots();
            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null)
                        continue;

                    operation.Initialize(id, responseCodec, hasResponsePayload, responseNullable);
                    var call = PendingCall.Rent(
                        this,
                        id,
                        kind,
                        operation,
                        dispatcher,
                        deadline,
                        cancellationToken,
                        completionObserver);
                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)
                    {
                        published = true;
                        registrationPublished = true;
                        try
                        {
                            OnRegistered(call);
                        }
                        catch (Exception registrationFailure)
                        {
                            // The publication barrier is set even when the owner callback
                            // throws before MarkRegistered. Complete through the table's
                            // authoritative terminal transition; never return a separate
                            // failed ValueTask with the published slot still occupied.
                            TryComplete(call.Id, PendingCallCompletionReason.SendFailure, registrationFailure);
                            if (kind == PendingCallKind.Unary &&
                                completionObserver is IPendingCallPostOperationObserver)
                            {
                                // Only this shape can hand the already-terminal operation to
                                // its caller without an outer logical-lifetime wrapper.
                                return true;
                            }

                            throw;
                        }

                        if (call.CancellationToken.IsCancellationRequested)
                            TryComplete(call.Id, PendingCallCompletionReason.UserCancellation);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }

                    call.ReturnUnused();
                }

                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
            }
        }
        catch
        {
            if (!published)
                ReleaseCapacity();
            throw;
        }
    }

    private bool TryRegister(
        PendingCallKind kind,
        IRpcOperation? operation,
        IStreamDispatcher? dispatcher,
        RpcDeadline deadline,
        CancellationToken cancellationToken,
        out long id,
        IPendingCallCompletionObserver? completionObserver = null)
    {
        if (!TryAcquireCapacity())
        {
            id = 0;
            return false;
        }

        var published = false;
        try
        {
            var slots = GetOrCreateSlots();
            while (true)
            {
                for (var attempt = 0; attempt < slots.Length; attempt++)
                {
                    id = NextRequestId();
                    var index = (int)(id & _indexMask);
                    if (Volatile.Read(ref slots[index]) is not null)
                        continue;

                    var call = PendingCall.Rent(
                        this,
                        id,
                        kind,
                        operation,
                        dispatcher,
                        deadline,
                        cancellationToken,
                        completionObserver);
                    if (Interlocked.CompareExchange(ref slots[index], call, null) is null)
                    {
                        published = true;
                        try
                        {
                            OnRegistered(call);
                        }
                        catch (Exception registrationFailure)
                        {
                            // Streams have no unary operation to return. Release their
                            // published slot but preserve the original registration failure.
                            TryComplete(call.Id, PendingCallCompletionReason.SendFailure, registrationFailure);
                            throw;
                        }

                        if (call.CancellationToken.IsCancellationRequested)
                            TryComplete(call.Id, PendingCallCompletionReason.UserCancellation);
                        CompleteRegistrationIfDisposed(call);
                        return true;
                    }

                    call.ReturnUnused();
                }

                // A capacity reservation guarantees that some physical slot is free. Concurrent
                // registrars can consume the request IDs that map to that slot, so retry another
                // bounded round instead of reporting false resource exhaustion.
                Thread.Yield();
            }
        }
        catch
        {
            if (!published)
                ReleaseCapacity();
            throw;
        }
    }

    private void OnRegistered(PendingCall call)
    {
        // A competing completion may have already claimed the slot and be waiting inside
        // WaitUntilRegistered(). Always publish the barrier, including when an extension
        // throws before the normal registration boundary.
        try
        {
            SharpLinkTelemetry.AddPendingRequests(1);
            _owner.OnPendingCallRegistered();
        }
        finally
        {
            call.MarkRegistered();
        }

        // A timer failure after publication is caught by the registering caller, which
        // terminalizes the pending slot. Keep cancellation/Dispose terminal transitions
        // outside that registration-only catch so cleanup invariant exceptions propagate.
        if (call.Deadline.HasValue)
            _deadlineScheduler.Observe(call.Deadline);
    }
}
