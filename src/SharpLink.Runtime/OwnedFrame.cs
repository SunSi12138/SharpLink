namespace SharpLink.Runtime;

/// <summary>Receives a queued request's failure while its send pump still owns the frame.</summary>
internal interface IRequestEmissionFailureObserver
{
    void OnRequestEmissionFailure(long requestId, Exception exception);
}

/// <summary>
/// Transfers one encoded frame and its backing writer or held page reference to the send pump.
/// Only the pump may release the owner after physical output has finished or the frame is drained.
/// </summary>
internal readonly struct OwnedFrame
{
    private readonly object _owner;
    private readonly object? _completionState;

    internal OwnedFrame(
        IRpcByteBufferWriter owner,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        bool isProtocolProgress,
        IRequestEmissionFailureObserver? failureObserver = null)
    {
        _owner = owner;
        Memory = owner.WrittenMemory;
        Length = owner.WrittenCount;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;

        _completionState = (object?)flushCompletion ?? failureObserver;
    }

    // Consumes one reference already acquired by the producer. Do not AddRef here:
    // admission and every failure/drain path transfer or release that same reference.
    internal OwnedFrame(
        EncodedStreamPage page,
        int offset,
        int length,
        bool forceFlush = false,
        TaskCompletionSource<bool>? flushCompletion = null,
        bool isProtocolProgress = false)
    {
        _owner = page;
        Memory = page.Buffer.AsMemory(offset, length);
        Length = length;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;
        _completionState = flushCompletion;
    }

    // A shared owner slot keeps legacy unary frame size unchanged. Page-specific
    // offset discovery happens only while considering an already-page-backed batch.
    public IRpcByteBufferWriter? Owner => _owner as IRpcByteBufferWriter;

    public EncodedStreamPage? Page => _owner as EncodedStreamPage;

    public int PageOffset
        => System.Runtime.InteropServices.MemoryMarshal.TryGetArray(Memory, out var segment)
            ? segment.Offset
            : throw new InvalidOperationException("Encoded stream pages require array-backed memory.");

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
