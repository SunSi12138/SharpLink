namespace SharpLink.Runtime;

/// <summary>
/// Transfers one encoded frame and its backing writer to the session send pump.
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
        RpcDeadline deadline = default)
    {
        Owner = owner;
        Memory = owner.WrittenMemory;
        Length = owner.WrittenCount;
        ForceFlush = forceFlush;
        IsProtocolProgress = isProtocolProgress;

        if (owner is PooledByteBufferWriter pooledOwner)
        {
            pooledOwner.EmissionDeadline = deadline;
            _completionState = flushCompletion;
        }
        else if (!deadline.HasValue)
        {
            _completionState = flushCompletion;
        }
        else if (flushCompletion is null)
        {
            _completionState = new DeadlineState(deadline);
        }
        else
        {
            _completionState = new CompletionDeadlineState(flushCompletion, deadline);
        }
    }

    public IRpcByteBufferWriter Owner { get; }

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length { get; }

    public bool ForceFlush { get; }

    public TaskCompletionSource<bool>? FlushCompletion
        => _completionState switch
        {
            TaskCompletionSource<bool> completion => completion,
            CompletionDeadlineState state => state.Completion,
            _ => null
        };

    /// <summary>
    /// The process-local request lifetime retained until the transport emission boundary.
    /// Default pooled writers retain it on the writer lease so this hot-path struct does not grow;
    /// custom writers fall back to the existing completion-state reference slot.
    /// </summary>
    public RpcDeadline Deadline
        => Owner is PooledByteBufferWriter pooledOwner
            ? pooledOwner.EmissionDeadline
            : _completionState switch
            {
                DeadlineState state => state.Deadline,
                CompletionDeadlineState state => state.Deadline,
                _ => default
            };

    /// <summary>
    /// True when the frame carries protocol progress (ping/pong, window
    /// update, go-away) rather than RPC data. The send pump admits and
    /// drains progress frames against a small reserved byte headroom and a
    /// bounded priority burst so stream saturation cannot starve them.
    /// </summary>
    public bool IsProtocolProgress { get; }

    private sealed class DeadlineState(RpcDeadline deadline)
    {
        internal RpcDeadline Deadline { get; } = deadline;
    }

    private sealed class CompletionDeadlineState(
        TaskCompletionSource<bool> completion,
        RpcDeadline deadline)
    {
        internal TaskCompletionSource<bool> Completion { get; } = completion;
        internal RpcDeadline Deadline { get; } = deadline;
    }
}
