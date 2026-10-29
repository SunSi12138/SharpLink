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
    private readonly object? _completionState;
    private readonly byte _flags;
    private const byte ForceFlushFlag = 1;
    private const byte ProtocolProgressFlag = 2;
    private const byte CoalescibleWindowUpdateFlag = 4;

    internal OwnedFrame(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        bool isProtocolProgress,
        IRequestEmissionFailureObserver? failureObserver = null,
        bool isCoalescibleWindowUpdate = false)
    {
        Owner = owner;
        Memory = owner.WrittenMemory;
        Length = owner.WrittenCount;
        _flags = (byte)((forceFlush ? ForceFlushFlag : 0) |
            (isProtocolProgress ? ProtocolProgressFlag : 0) |
            (isCoalescibleWindowUpdate ? CoalescibleWindowUpdateFlag : 0));

        _completionState = (object?)flushCompletion ?? failureObserver;
    }

    public IRpcByteBufferWriter Owner { get; }

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length { get; }

    public bool ForceFlush => (_flags & ForceFlushFlag) != 0;

    /// <summary>
    /// True when the frame carries protocol progress (ping/pong, window
    /// update, go-away) rather than RPC data. The send pump admits and
    /// drains progress frames against a small reserved byte headroom and a
    /// bounded priority burst so stream saturation cannot starve them.
    /// </summary>
    public bool IsProtocolProgress => (_flags & ProtocolProgressFlag) != 0;

    // Only the runtime's receive-credit emitter opts in. Canonical-looking arbitrary
    // packets are not sufficient provenance: duplicate credits need not compose.
    internal bool IsCoalescibleWindowUpdate => (_flags & CoalescibleWindowUpdateFlag) != 0;

    public TaskCompletionSource<bool>? FlushCompletion
        => _completionState as TaskCompletionSource<bool>;

    public IRequestEmissionFailureObserver? FailureObserver
        => _completionState as IRequestEmissionFailureObserver;
}
