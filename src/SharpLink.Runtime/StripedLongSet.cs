namespace SharpLink.Runtime;

public sealed class StripedLongSet
{
    private readonly Lock[] _locks;
    private readonly HashSet<long>[] _sets;
    private readonly int _stripeMask;

    public StripedLongSet() : this(new RuntimeConcurrencyOptions())
    {
    }

    public StripedLongSet(RuntimeConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options.CloneValidated();
        _locks = new Lock[snapshot.StripeCount];
        _sets = new HashSet<long>[snapshot.StripeCount];
        _stripeMask = snapshot.StripeCount - 1;
        for (var i = 0; i < snapshot.StripeCount; i++)
        {
            _locks[i] = new Lock();
            _sets[i] = [];
        }
    }

    public bool Add(long value)
    {
        var stripe = GetStripe(value);
        lock (_locks[stripe])
            return _sets[stripe].Add(value);
    }

    public bool Remove(long value)
    {
        var stripe = GetStripe(value);
        lock (_locks[stripe])
            return _sets[stripe].Remove(value);
    }

    /// <summary>Returns whether the set currently contains <paramref name="value"/>.</summary>
    public bool Contains(long value)
    {
        var stripe = GetStripe(value);
        lock (_locks[stripe])
            return _sets[stripe].Contains(value);
    }

    public void Clear()
    {
        for (var i = 0; i < _sets.Length; i++)
        {
            lock (_locks[i])
                _sets[i].Clear();
        }
    }

    private int GetStripe(long value)
    {
        var hash = unchecked((int)(value ^ (value >> 32)));
        hash &= int.MaxValue;
        return hash & _stripeMask;
    }
}
