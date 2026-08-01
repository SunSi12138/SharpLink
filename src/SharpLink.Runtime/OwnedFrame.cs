namespace SharpLink.Runtime;

/// <summary>
/// Transfers one encoded frame and its backing writer to the session send pump.
/// Only the pump may return the owner after the frame has been flushed or drained.
/// </summary>
internal readonly struct OwnedFrame(
    IRpcByteBufferWriter owner,
    bool forceFlush,
    TaskCompletionSource<bool>? flushCompletion)
{
    public IRpcByteBufferWriter Owner { get; } = owner;

    public ReadOnlyMemory<byte> Memory { get; } = owner.WrittenMemory;

    public int Length { get; } = owner.WrittenCount;

    public bool ForceFlush { get; } = forceFlush;

    public TaskCompletionSource<bool>? FlushCompletion { get; } = flushCompletion;
}
