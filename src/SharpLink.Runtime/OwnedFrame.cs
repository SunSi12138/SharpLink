namespace SharpLink.Runtime;

/// <summary>Receives a queued request's failure while its send pump still owns the frame.</summary>
internal interface IRequestEmissionFailureObserver
{
    void OnRequestEmissionFailure(long requestId, Exception exception);
}

/// <summary>
/// Transfers one encoded frame or trusted typed credit to the session send pump.
/// Only the pump may return the owner after the frame has been flushed or drained.
/// </summary>
internal readonly struct OwnedFrame
{
    private readonly object? _completionState;

    internal OwnedFrame(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        bool isProtocolProgress,
        IRequestEmissionFailureObserver? failureObserver = null)
    {
        Owner = owner;
        Memory = owner.WrittenMemory;
        Length = owner.WrittenCount;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;

        _completionState = (object?)flushCompletion ?? failureObserver;
    }

    internal OwnedFrame(OutgoingWindowUpdate windowUpdate)
    {
        Owner = null;
        Memory = default;
        Length = OutgoingWindowUpdate.FrameBytes;
        ForceFlush = false;
        IsProtocolProgress = true;
        _completionState = windowUpdate;
    }

    public IRpcByteBufferWriter? Owner { get; }

    internal OutgoingWindowUpdate WindowUpdate
        => _completionState as OutgoingWindowUpdate ??
           throw new InvalidOperationException("The frame does not own a typed WindowUpdate.");

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
        => _completionState as TaskCompletionSource<bool>;

    public IRequestEmissionFailureObserver? FailureObserver
        => _completionState as IRequestEmissionFailureObserver;
}
