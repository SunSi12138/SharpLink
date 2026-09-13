namespace SharpLink.Client;

/// <summary>
/// Request publication for <see cref="PendingRequestTable"/>.
/// </summary>
partial class PendingRequestTable
{
    /// <summary>
    /// Publishes one Request frame under the owning call's completion gate.
    /// </summary>
    /// <remarks>
    /// The gate is the ordering authority between "this Request reached the session send queue" and
    /// "this call produced a terminal reason that has to tell the peer". A terminal transition that
    /// wins the race removes the slot first, so this method then refuses to publish and the caller
    /// drops the frame instead of delivering a Request after its own cancel. A publication that
    /// wins marks <see cref="PendingCall.RequestPublished"/> only for an accepted frame and before
    /// the gate is released, so a later terminal transition still emits its cancel - behind the
    /// Request, because both frames share the session's normal queue.
    /// </remarks>
    /// <param name="writer">
    /// A frame that the caller already prepared outside this gate. It owns the packet on entry, and
    /// every path except a refused publication either queues it or returns it.
    /// </param>
    /// <param name="id">The request identifier whose pending slot gates this publication.</param>
    /// <param name="session">The session the prepared frame has to be admitted to.</param>
    /// <param name="observeEmission">Whether the caller needs a waiter for the transport write.</param>
    /// <param name="cancellationToken">Caller cancellation observed while waiting for emission.</param>
    /// <param name="failureObserver">Observer notified when the queued frame fails in the transport.</param>
    /// <param name="emission">
    /// The emission waiter for an accepted frame, or <c>default</c> for every other outcome.
    /// </param>
    /// <param name="rejection">
    /// Set when the session refused admission and already returned the frame. The caller owns the
    /// frame on every other path.
    /// </param>
    /// <returns><c>true</c> when the frame reached the send queue.</returns>
    public bool TryPublishRequest(
        long id,
        RpcSession session,
        IRpcByteBufferWriter writer,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        out ValueTask emission,
        out Exception? rejection)
    {
        emission = default;
        rejection = null;
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
            return false;

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is null || current.Id != id)
            return false;

        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id)
                return false;

            // Admission is the linearization point and it is synchronous: the frame is either in the
            // queue now or it was already returned. Marking the call published on a faulted or
            // deferred send is what produced a cancel for a Request the peer never received.
            if (!session.TryEnqueuePreparedFrame(
                    writer,
                    observeEmission,
                    cancellationToken,
                    failureObserver,
                    out emission,
                    out rejection))
            {
                emission = default;
                return false;
            }

            current.MarkRequestPublished();
            return true;
        }
    }
}
