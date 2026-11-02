namespace SharpLink.Client;

partial class PendingRequestTable
{
    /// <summary>
    /// Takes the prepared writer on entry on EVERY outcome. Preparation/reservation and final
    /// callbacks are outside CompletionGate; only callback-free queue transfer and scalar exact
    /// head disposition commit run inside. Accepted ownership survives deferred callback failure.
    /// </summary>
    public bool TryPublishRequest(
        long id,
        RpcSession session,
        IRpcByteBufferWriter writer,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        out ValueTask emission,
        out Exception? rejection,
        RpcFramePublication? publication = null,
        RpcHeadEmissionObservation observation = RpcHeadEmissionObservation.CallerWait)
    {
        emission = default;
        rejection = null;
        var admission = session.PrepareFrameEnqueue(writer, observeEmission, cancellationToken,
            failureObserver, publication, publication is null ? RpcFramePublicationRole.None : RpcFramePublicationRole.RequestHead);
        var group = publication as ClientStreamPublicationGroup;
        PendingCall? terminal = null;
        PendingCallCompletionReason terminalReason = default;
        Exception? terminalException = null;
        Exception? failure = null;
        try
        {
            // Fallible preparation is before transfer and inside unconditional finalization.
            // The existing completion is rooted even if any deferred accepted callback throws.
            if (observation == RpcHeadEmissionObservation.PendingOwnedHead && admission.Completion is { } completion)
            {
                if (group is null) throw new InvalidOperationException("A pending-owned head requires its exact stream group.");
                group.PrepareHeadEmission(session, id, completion.Task);
            }
            var slots = Volatile.Read(ref _slots);
            var index = (int)(id & _indexMask);
            var current = slots is null ? null : Volatile.Read(ref slots[index]);
            if (current is not null && current.Id == id)
            {
                // Virtual clock sampling belongs outside the commit gate. Reentrant cancellation
                // may remove this exact call; only the subsequent scalar/full-ID recheck commits.
                // No guarantee is made about wall time advancing across a scheduler pause here.
                long timestamp;
                try { timestamp = current.Deadline.HasValue ? _timeProvider.GetTimestamp() : 0; }
                catch (Exception error)
                {
                    terminal = TakeCallAfterClockFailure(slots!, index, current, id);
                    terminalReason = PendingCallCompletionReason.SendFailure;
                    terminalException = error;
                    throw;
                }
                lock (current.CompletionGate)
                {
                    if (ReferenceEquals(Volatile.Read(ref slots![index]), current) && current.Id == id &&
                        (group is null || ReferenceEquals(current.StreamGroup, group)))
                    {
                        var expired = current.Deadline.IsExpired(timestamp);
                        if (expired || current.CancellationToken.IsCancellationRequested)
                        {
                            terminalReason = expired ? PendingCallCompletionReason.DeadlineExceeded : PendingCallCompletionReason.UserCancellation;
                            if (TryRemoveTerminalCall(slots!, index, current))
                            {
                                current.WaitUntilRegistered();
                                group?.ResolveHeadUnderCompletionGate(accepted: false);
                                terminal = current;
                            }
                        }
                        else
                        {
                            var accepted = session.TryTransferPreparedFrame(ref admission);
                            if (accepted)
                            {
                                // Existing pending -> group gate order; validated scalar commit only.
                                if (observation == RpcHeadEmissionObservation.PendingOwnedHead && admission.Completion is not null)
                                    group!.CommitHeadEmissionUnderCompletionGate();
                                current.MarkRequestPublished();
                            }
                            group?.ResolveHeadUnderCompletionGate(accepted);
                        }
                    }
                    // A prior terminal committed DefinitelyNeverAccepted under this same gate.
                }
            }
        }
        catch (Exception error) { failure = error; }
        // A removed/replaced original call (including a terminal selected above) never
        // attempted transfer. Its immutable logical terminal owns this head disposition;
        // capacity sampled during preparation must not manufacture a second send failure.
        if (failure is null && !admission.TransferAttempted)
            admission.AbandonForTerminal();
        // All independent deferred obligations survive errors in the other obligations.
        try { session.FinishPreparedFrameEnqueue(ref admission, out emission, out rejection, observation); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (!admission.Accepted && admission.Completion is not null && observation == RpcHeadEmissionObservation.PendingOwnedHead)
            group?.RetireUnacceptedHeadEmission();
        try { group?.NotifyHeadResolved(); }
        catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        if (terminal is not null)
        {
            try
            {
                var empty = ReadOnlySequence<byte>.Empty;
                CompleteTakenCall(terminal, terminalReason, terminalException, ref empty);
            }
            catch (Exception error) { failure = RpcPublicationErrors.Combine(failure, error); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return admission.Accepted;
    }
}
