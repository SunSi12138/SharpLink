namespace SharpLink.Runtime;

// The local publisher boundary is the actual prepared-frame append. Retire
// joins only publishers already admitted there, never a compressor or old call.
internal sealed class NStudyClientRoute(RpcSession session, NStudyAck ack)
{
    private readonly Lock _gate = new();
    private bool _retired;
    private int _publishers;
    private TaskCompletionSource? _drained;

    internal long Token => ack.BindingId;
    internal NStudyTarget Target => ack.Target;
    internal RpcHash128 Hash => ack.Hash;
    internal NStudyAck Acknowledgement => ack;

    // Selection checks a snapshot; only TryEnter grants authority to append.
    // Retire still closes that gate and drains the publishers admitted under it.
    internal bool IsOpenOn(RpcSession owner)
        => ReferenceEquals(owner, session) && !Volatile.Read(ref _retired);

    internal bool TryEnter(RpcSession owner)
    {
        lock (_gate)
        {
            if (_retired || !ReferenceEquals(owner, session))
                return false;
            checked { _publishers++; }
            return true;
        }
    }

    internal void Leave()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (--_publishers == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }

    internal Task Retire()
    {
        lock (_gate)
        {
            Volatile.Write(ref _retired, true);
            return _publishers == 0
                ? Task.CompletedTask
                : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
}
