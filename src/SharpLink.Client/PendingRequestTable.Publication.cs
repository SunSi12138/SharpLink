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
    /// wins marks <see cref="PendingCall.RequestPublished"/> before the gate is released, so a later
    /// terminal transition still emits its cancel - behind the Request, because both frames share
    /// the session's normal queue.
    /// </remarks>
    public bool TryPublishRequest(
        long id,
        RpcSession session,
        IRpcByteBufferWriter writer,
        bool observeEmission,
        CancellationToken cancellationToken,
        IRequestEmissionFailureObserver? failureObserver,
        out ValueTask emission)
    {
        emission = default;
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

            // The session owns the writer from this point, including the paths that fail before the
            // frame is queued and return it themselves.
            if (observeEmission)
                emission = session.SendPacketAndObserveEmissionAsync(writer, cancellationToken);
            else
                session.SendPacket(writer, failureObserver);

            current.MarkRequestPublished();
            return true;
        }
    }
}
