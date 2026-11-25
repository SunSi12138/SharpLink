namespace SharpLink.Runtime;

// The local publisher boundary is the actual prepared-frame append. Retire
// joins only publishers already admitted there, never a compressor or old call.
internal sealed class NStudyClientRoute(RpcSession session, NStudyAck ack)
{
    private const int Retired = int.MinValue;
    // The sign bit closes admission; the remaining bits count admitted appends.
    private int _state;
    private TaskCompletionSource? _drained;

    internal long Token => ack.BindingId;
    internal NStudyTarget Target => ack.Target;
    internal RpcHash128 Hash => ack.Hash;
    internal NStudyAck Acknowledgement => ack;

    // Selection checks a snapshot; only TryEnter grants authority to append.
    // Retire closes that gate and drains the publishers admitted before its cut.
    internal bool IsOpenOn(RpcSession owner)
        => ReferenceEquals(owner, session) && Volatile.Read(ref _state) >= 0;

    internal bool TryEnter(RpcSession owner)
    {
        if (!ReferenceEquals(owner, session))
            return false;

        var state = Volatile.Read(ref _state);
        while (state >= 0)
        {
            var observed = Interlocked.CompareExchange(ref _state, checked(state + 1), state);
            if (observed == state)
                return true;
            state = observed;
        }
        return false;
    }

    internal void Leave()
    {
        if (Interlocked.Decrement(ref _state) == Retired)
            Volatile.Read(ref _drained)?.TrySetResult();
    }

    internal Task Retire()
    {
        var prior = Interlocked.Or(ref _state, Retired);
        if ((prior & int.MaxValue) == 0)
            return Task.CompletedTask;

        var drained = LazyInitializer.EnsureInitialized(ref _drained,
            static () => new(TaskCreationOptions.RunContinuationsAsynchronously));
        // Pair the last Leave's waiter check with a count check after publishing the waiter.
        if (Volatile.Read(ref _state) == Retired)
            drained.TrySetResult();
        return drained.Task;
    }
}
