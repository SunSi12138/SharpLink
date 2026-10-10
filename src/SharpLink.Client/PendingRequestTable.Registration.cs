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
                            OnRegistered(call, deadline);
                        }
                        catch (Exception registrationFailure)
                        {
                            // The publication barrier is set even when the owner callback
                            // throws before MarkRegistered. Complete through the table's
                            // authoritative terminal transition; never return a separate
                            // failed ValueTask with the published slot still occupied.
                            CompleteFailedRegistration(id, registrationFailure);
                            if (kind == PendingCallKind.Unary &&
                                completionObserver is IPendingCallPostOperationObserver)
                            {
                                // Only this shape can hand the already-terminal operation to
                                // its caller without an outer logical-lifetime wrapper.
                                return true;
                            }

                            throw;
                        }

                        if (cancellationToken.IsCancellationRequested)
                            TryComplete(id, PendingCallCompletionReason.UserCancellation);
                        CompleteRegistrationIfDisposed(id);
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
                            OnRegistered(call, deadline);
                        }
                        catch (Exception registrationFailure)
                        {
                            // Streams have no unary operation to return. Release their
                            // published slot but preserve the original registration failure.
                            CompleteFailedRegistration(id, registrationFailure);
                            throw;
                        }

                        if (cancellationToken.IsCancellationRequested)
                            TryComplete(id, PendingCallCompletionReason.UserCancellation);
                        CompleteRegistrationIfDisposed(id);
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

    /// <summary>
    /// Terminalizes a registration that already published its slot, without consulting the
    /// potentially failing application TimeProvider again. Normal deadline arbitration remains
    /// authoritative for successfully registered calls. The saved request ID, not the pooled
    /// PendingCall's mutable Id, prevents terminating a later renter after a concurrent winner
    /// has returned the original call to its pool.
    /// </summary>
    private bool CompleteFailedRegistration(long requestId, Exception registrationFailure)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(requestId & _indexMask);
        while (true)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null || current.Id != requestId)
                return false;

            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||
                    current.Id != requestId)
                {
                    continue;
                }

                if (!ReferenceEquals(
                        Interlocked.CompareExchange(ref slots[index], null, current), current))
                {
                    continue;
                }

                // OnRegistered always signals this gate from finally. A competing Dispose
                // or cancellation may have already taken the call; in that case we lose the
                // slot CAS and leave its chosen terminal result untouched.
                current.WaitUntilRegistered();
            }

            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(current, PendingCallCompletionReason.SendFailure,
                registrationFailure, ref payload);
            return true;
        }
    }

    private void OnRegistered(PendingCall call, RpcDeadline deadline)
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
        if (deadline.HasValue)
            _deadlineScheduler.Observe(deadline);
    }
}
