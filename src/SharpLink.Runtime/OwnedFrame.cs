namespace SharpLink.Runtime;

/// <summary>Receives a queued request's failure while its send pump still owns the frame.</summary>
internal interface IRequestEmissionFailureObserver
{
    void OnRequestEmissionFailure(long requestId, Exception exception);
}

/// <summary>
/// Transfers one encoded frame and its backing writer to the session send pump.
/// Only the pump may return the owner after the frame has been flushed or drained.
/// </summary>
internal readonly struct OwnedFrame
{
    private readonly TaskCompletionSource<bool>? _flushCompletion;
    private readonly IRequestEmissionFailureObserver? _failureObserver;

    internal OwnedFrame(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        bool isProtocolProgress,
        IRequestEmissionFailureObserver? failureObserver = null,
        RpcFramePublication? publication = null,
        RpcFramePublicationRole publicationRole = RpcFramePublicationRole.None)
    {
        Owner = owner;
        Memory = owner.WrittenMemory;
        Length = owner.WrittenCount;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;

        _flushCompletion = flushCompletion;
        _failureObserver = failureObserver;
        Publication = publication;
        PublicationRole = publicationRole;
        // Captured before the pump can return the writer; never parse a released buffer
        // from producer-side post-transfer notification.
        StreamId = publicationRole is RpcFramePublicationRole.StreamData or RpcFramePublicationRole.StreamTerminal
            ? BinaryPrimitives.ReadUInt16LittleEndian(Memory.Span.Slice(ProtocolV2Constants.HeaderBytes, sizeof(ushort)))
            : (ushort)0;
    }

    public IRpcByteBufferWriter Owner { get; }

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length { get; }

    public bool ForceFlush { get; }

    /// <summary>
    /// True when the frame carries protocol progress (ping/pong, window
    /// update, go-away) rather than RPC data. The send pump admits and
    /// drains progress frames against a small reserved byte headroom and a
    /// bounded priority burst so stream saturation cannot starve them.
    /// </summary>
    public bool IsProtocolProgress { get; }

    public TaskCompletionSource<bool>? FlushCompletion
        => _flushCompletion;

    public IRequestEmissionFailureObserver? FailureObserver
        => _failureObserver;

    internal RpcFramePublication? Publication { get; }
    internal RpcFramePublicationRole PublicationRole { get; }
    internal ushort StreamId { get; }
}
