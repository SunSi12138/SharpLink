namespace SharpLink.Client;

partial class PendingRequestTable
{
    // Type22 is nonterminal. No observer, producer, cancellation callback or user code runs
    // under CompletionGate. The existing full-ID table is also the only ACK lookup authority.
    internal void ReceiveRequestAccepted(ulong wireId)
    {
        if (wireId == 0 || wireId > long.MaxValue || wireId > (ulong)HighestAllocatedRequestId)
            throw InvalidAcceptance("RequestAccepted has an unallocated request ID.");
        var id = (long)wireId;
        var slots = Volatile.Read(ref _slots);
        if (slots is null) return;
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id) return; // Old/skipped ID; never touch its replacement.
        TaskCompletionSource<bool>? signal = null;
        PendingCall? terminal = null;
        PendingCallCompletionReason reason = default;
        // The virtual clock is an extension boundary. Observe immediately before the gate,
        // then validate the exact incarnation again; reentrant terminalization cannot commit
        // acceptance/start against the removed call. This is a sampled boundary, not a claim
        // that wall time cannot advance while the thread is descheduled before acquisition.
        long timestamp;
        try { timestamp = call.Deadline.HasValue ? _timeProvider.GetTimestamp() : 0; }
        catch (Exception error)
        {
            CompleteClockSamplingFailure(slots, index, call, id, error);
            throw; // CompleteClockSamplingFailure preserves the original error and always throws.
        }
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id) return;
            if (call.Kind is not (PendingCallKind.ClientStreaming or PendingCallKind.DuplexStreaming))
                throw InvalidAcceptance("RequestAccepted is illegal for this live call kind.");
            if (call.PeerAccepted)
                throw InvalidAcceptance("Duplicate RequestAccepted for a live call.");
            var expired = call.Deadline.IsExpired(timestamp);
            if (expired || call.CancellationToken.IsCancellationRequested)
            {
                reason = expired
                    ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.UserCancellation;
                if (TryRemoveTerminalCall(slots, index, call))
                {
                    call.WaitUntilRegistered();
                    call.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
                    terminal = call;
                }
            }
            else
            {
                call.PeerAccepted = true;
                signal = call.AcceptanceReady;
            }
        }
        signal?.TrySetResult(true);
        if (terminal is not null)
        {
            var empty = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(terminal, reason, null, ref empty);
        }
    }

    internal ValueTask<bool> WaitForProducerStartAsync(long id, ClientStreamPublicationGroup group)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null) return ValueTask.FromResult(false);
        var call = Volatile.Read(ref slots[(int)(id & _indexMask)]);
        if (call is null || call.Id != id) return ValueTask.FromResult(false);
        Task<bool>? readiness;
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[(int)(id & _indexMask)]), call) ||
                call.Id != id || !ReferenceEquals(call.StreamGroup, group)) return ValueTask.FromResult(false);
            readiness = call.AcceptanceReady?.Task;
        }
        return readiness is null ? ValueTask.FromResult(TryClaimProducerStart(id, group)) : AwaitAcceptanceAsync(readiness, id, group);
    }

    private async ValueTask<bool> AwaitAcceptanceAsync(Task<bool> readiness, long id, ClientStreamPublicationGroup group)
        => await readiness.ConfigureAwait(false) && TryClaimProducerStart(id, group);

    private bool TryClaimProducerStart(long id, ClientStreamPublicationGroup group)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null) return false;
        var index = (int)(id & _indexMask);
        var call = Volatile.Read(ref slots[index]);
        if (call is null || call.Id != id) return false;
        PendingCall? terminal = null;
        PendingCallCompletionReason reason = default;
        // The virtual clock is an extension boundary. Observe immediately before the gate,
        // then validate the exact incarnation again; reentrant terminalization cannot commit
        // acceptance/start against the removed call. This is a sampled boundary, not a claim
        // that wall time cannot advance while the thread is descheduled before acquisition.
        long timestamp;
        try { timestamp = call.Deadline.HasValue ? _timeProvider.GetTimestamp() : 0; }
        catch (Exception error)
        {
            CompleteClockSamplingFailure(slots, index, call, id, error);
            throw; // CompleteClockSamplingFailure preserves the original error and always throws.
        }
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id ||
                !ReferenceEquals(call.StreamGroup, group) || call.ProducerStarted) return false;
            var expired = call.Deadline.IsExpired(timestamp);
            if (expired || call.CancellationToken.IsCancellationRequested)
            {
                reason = expired ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.UserCancellation;
                if (TryRemoveTerminalCall(slots, index, call))
                {
                    call.WaitUntilRegistered();
                    group.ResolveHeadUnderCompletionGate(accepted: false);
                    terminal = call;
                }
            }
            else if (call.RequestPublished && group.HeadPublicationConfirmed &&
                (call.Kind == PendingCallKind.OneWayClientStreaming || call.PeerAccepted))
            {
                call.ProducerStarted = true;
                return true;
            }
        }
        if (terminal is not null)
        {
            var empty = ReadOnlySequence<byte>.Empty;
            CompleteTakenCall(terminal, reason, null, ref empty);
        }
        return false;
    }

    // A broken clock cannot force terminal cleanup to resample the same broken extension.
    // Detach only this full-ID incarnation using callback-free state; all notifications remain
    // outside the gate and independently preserve their failures.
    private static PendingCall? TakeCallAfterClockFailure(PendingCall?[] slots, int index, PendingCall call, long id)
    {
        lock (call.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), call) || call.Id != id ||
                !TryRemoveTerminalCall(slots, index, call)) return null;
            call.WaitUntilRegistered();
            call.StreamGroup?.ResolveHeadUnderCompletionGate(accepted: false);
            return call;
        }
    }

    private void CompleteClockSamplingFailure(PendingCall?[] slots, int index, PendingCall call, long id, Exception error)
    {
        var terminal = TakeCallAfterClockFailure(slots, index, call, id);
        Exception? failure = error;
        if (terminal is not null)
        {
            try
            {
                var empty = ReadOnlySequence<byte>.Empty;
                CompleteTakenCall(terminal, PendingCallCompletionReason.SendFailure, error, ref empty);
            }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
    }

    private static SharpLinkException InvalidAcceptance(string message)
        => new(SharpLinkErrorCode.ProtocolViolation, message);
}
