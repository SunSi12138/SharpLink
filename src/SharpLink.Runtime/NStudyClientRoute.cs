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

    internal bool IsOpenOn(RpcSession owner)
    {
        lock (_gate)
            return !_retired && ReferenceEquals(owner, session);
    }

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
            _retired = true;
            return _publishers == 0
                ? Task.CompletedTask
                : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
}
