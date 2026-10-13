namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    /// <summary>
    /// Waits within the existing bounded pending slot. Its deadline, cancellation and terminal
    /// completion release this gate; the gate owns no separate timer or unbounded queue.
    /// </summary>
    internal ValueTask<bool> WaitForRequestAdmissionAsync(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return ValueTask.FromResult(false);
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id)
            return ValueTask.FromResult(false);
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id)
                return ValueTask.FromResult(false);
            if (call.Kind is not (PendingCallKind.ClientStreaming or PendingCallKind.DuplexStreaming))
                throw new InvalidOperationException("Only bidirectional client streams await request admission.");
            return call.WaitForRequestAdmissionAsync();
        }
    }

    /// <summary>Accepts a stream only on its original physical session and live pending generation.</summary>
    internal void AcceptRequest(long id)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return;
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id)
            return;
        PendingCall? expired = null;
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id)
                return;
            if (call.Kind is not (PendingCallKind.ClientStreaming or PendingCallKind.DuplexStreaming) ||
                !call.RequestPublished)
            {
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation,
                    "Request admission acknowledgement does not match a published client stream.");
            }
            if (call.Deadline.IsExpired(_timeProvider))
            {
                if (!ReferenceEquals(Interlocked.CompareExchange(ref slots[index], null, call), call))
                    return;
                call.WaitUntilRegistered();
                expired = call;
            }
            else if (!call.TryAcceptRequest())
            {
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation,
                    "A request admission acknowledgement was received more than once.");
            }
        }
        if (expired is not null)
        {
            var payload = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(expired, PendingCallCompletionReason.DeadlineExceeded, null, ref payload);
        }
    }
}
