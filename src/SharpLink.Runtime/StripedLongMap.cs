namespace SharpLink.Runtime;

public sealed class StripedLongMap<TValue> where TValue : class
{
    private readonly Lock[] _locks;
    private readonly Dictionary<long, TValue>[] _maps;
    private readonly int _stripeMask;

    public StripedLongMap()
    {
        var snapshot = RuntimeConcurrency.Snapshot();
        _locks = new Lock[snapshot.StripeCount];
        _maps = new Dictionary<long, TValue>[snapshot.StripeCount];
        _stripeMask = snapshot.StripeCount - 1;

        for (var i = 0; i < snapshot.StripeCount; i++)
        {
            _locks[i] = new Lock();
            _maps[i] = snapshot.InitialMapCapacityPerStripe == 0
                ? []
                : new Dictionary<long, TValue>(snapshot.InitialMapCapacityPerStripe);
        }
    }

    public void Set(long key, TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
            _maps[stripe][key] = value;
    }

    public TValue GetOrAdd(long key, Func<long, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);

        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            if (_maps[stripe].TryGetValue(key, out var existing))
                return existing;

            var created = valueFactory(key);
            _maps[stripe][key] = created;
            return created;
        }
    }

    public bool TryGetValue(long key, out TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
            return _maps[stripe].TryGetValue(key, out value!);
    }

    public bool TryRemove(long key, out TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
            return _maps[stripe].TryGetValue(key, out value!) && _maps[stripe].Remove(key);
    }

    public List<TValue> DrainValues()
    {
        var values = new List<TValue>();
        for (var i = 0; i < _maps.Length; i++)
        {
            lock (_locks[i])
            {
                if (_maps[i].Count == 0)
                    continue;

                values.AddRange(_maps[i].Values);
                _maps[i].Clear();
            }
        }

        return values;
    }

    private int GetStripe(long key)
    {
        var hash = unchecked((int)(key ^ (key >> 32)));
        hash &= int.MaxValue;
        return hash & _stripeMask;
    }
}
