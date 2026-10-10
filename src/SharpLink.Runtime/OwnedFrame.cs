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
    private readonly int _writerReadyCreditBytes;

    internal IWriterReadyCompletion? WriterReadyCompletion
        => _completionState as IWriterReadyCompletion;

    internal int WriterReadyCreditBytes => _writerReadyCreditBytes;

    internal OwnedFrame(WriterReadyFrame ready)
        : this(ready.Packet, ready.ForceFlush, null, false)
    {
        _completionState = ready.Completion;
        _writerReadyCreditBytes = ready.CreditBytes;
    }

    internal OwnedFrame(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        bool isProtocolProgress,
        IRequestEmissionFailureObserver? failureObserver = null)
    {
        Owner = owner;
        Memory = owner.WrittenMemory;
        _writerReadyCreditBytes = 0;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;
        _completionState = (object?)flushCompletion ?? failureObserver;
    }

    public IRpcByteBufferWriter Owner { get; }

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    public bool ForceFlush { get; }

    /// <summary>
    /// True for protocol progress (ping/pong, window update, go-away) rather
    /// than RPC data. The pump preserves reserved headroom and bounded service.
    /// </summary>
    public bool IsProtocolProgress { get; }

    public TaskCompletionSource<bool>? FlushCompletion
        => _completionState as TaskCompletionSource<bool>;

    public IRequestEmissionFailureObserver? FailureObserver
        => _completionState as IRequestEmissionFailureObserver;
}
