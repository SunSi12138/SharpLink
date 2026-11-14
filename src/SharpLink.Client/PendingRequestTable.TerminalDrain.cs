namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    private void CompleteTransportFailure(long id, PendingCallCompletionReason reason, Exception? exception)
    {
        List<Exception>? failures = null;
        if (!TryTakeTransportFailure((int)(id & _indexMask), id, out var call, out var expired, ref failures))
            return;
        if (expired)
        {
            reason = PendingCallCompletionReason.DeadlineExceeded;
            exception = null;
        }
        CompleteTransportFailureCall(call!, reason, exception, ref failures);
        ThrowTerminalCleanupFailures(failures);
    }

    // Only transport-terminal paths use this fallback. Normal response/cancel/deadline races
    // retain their authoritative clock sample and existing completion implementation.
    private bool TryTakeTransportFailure(int index, long? expectedId, out PendingCall? call,
        out bool deadlineExpired, ref List<Exception>? failures)
    {
        var slots = Volatile.Read(ref _slots);
        while (slots is not null)
        {
            var current = Volatile.Read(ref slots[index]);
            if (current is null || expectedId is { } id && current.Id != id)
                break;
            lock (current.CompletionGate)
            {
                if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) ||
                    expectedId is { } requestId && current.Id != requestId)
                    continue;

                Exception? clockFailure = null;
                deadlineExpired = false;
                try { deadlineExpired = current.Deadline.IsExpired(_timeProvider); }
                catch (Exception exception) { clockFailure = exception; }
                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, current), current))
                    continue;
                current.WaitUntilRegistered();
                call = current;
                if (clockFailure is not null) (failures ??= []).Add(clockFailure);
                // Unknown expiry is not evidence of a timeout. Keep the caller's transport
                // terminal reason and expose the clock fault separately from joined cleanup.
                return true;
            }
        }
        call = null;
        deadlineExpired = false;
        return false;
    }

    private void CompleteTransportFailureCall(PendingCall call, PendingCallCompletionReason reason,
        Exception? exception, ref List<Exception>? failures)
    {
        try { call.DisposeCancellationRegistration(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try
        {
            var producerFailure = call.CancelProducer(reason);
            if (producerFailure is not null)
            {
                try { _owner.OnProducerCancellationCallbackFailed(producerFailure); }
                catch { /* Preserve the existing diagnostic-isolation contract. */ }
            }
        }
        catch (Exception failure) { (failures ??= []).Add(failure); }

        exception ??= CreateCompletionException(call, reason);
        var completion = new PendingCallCompletion(call.Id, call.Kind, reason, call.Dispatcher, exception, call.RequestPublished);
        // AttemptOutcomeState can fail while sampling elapsed time before its policy catch.
        // An observer owns diagnostics, not permission to strand an already-removed operation.
        try { call.CompletionObserver?.OnPendingCallCompleted(in completion); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try
        {
            call.Operation?.SetError(exception ?? new SharpLinkException(SharpLinkErrorCode.Internal,
                "A pending request completed without a result."));
        }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { _owner.OnPendingCallCompleted(in completion); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { call.ReturnCompleted(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        try { ReleaseSlot(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }

    private static void ThrowTerminalCleanupFailures(List<Exception>? failures)
    {
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }
}
