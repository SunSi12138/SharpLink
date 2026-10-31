namespace SharpLink.Runtime;

/// <summary>A physical-session issuance domain. Exhaustion is permanent, never a wrapping counter.</summary>
internal sealed class Protocol3IdSequence
{
    private long _last;

    internal Protocol3IdSequence(long lastIssued = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastIssued);
        _last = lastIssued;
    }

    internal long LastIssued => Volatile.Read(ref _last);
    internal bool IsExhausted => LastIssued == long.MaxValue;

    internal bool TryIssue(out long id)
    {
        while (true)
        {
            var previous = Volatile.Read(ref _last);
            if (previous == long.MaxValue)
            {
                id = 0;
                return false;
            }
            var next = previous + 1;
            if (Interlocked.CompareExchange(ref _last, next, previous) == previous)
            {
                id = next;
                return true;
            }
        }
    }
}
