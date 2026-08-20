namespace SharpLink.Runtime;

internal sealed class StripedLongMap<TValue> where TValue : class
{
    private readonly Lock[] _locks;
    private readonly Dictionary<long, TValue>[] _maps;
    private readonly int _stripeMask;
    private int _count;
    private bool _countTrackingEnabled;

    public StripedLongMap() : this(new RuntimeConcurrencyOptions())
    {
    }

    public StripedLongMap(RuntimeConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options.CloneValidated();
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

    /// <summary>
    /// Enables a cheap occupancy hint for owners that need it. This must be called before the map
    /// is published for concurrent mutation; maps that never opt in pay no shared atomic writes.
    /// Existing entries are included in the initial hint.
    /// </summary>
    internal void EnableCountTracking()
    {
        if (_countTrackingEnabled)
            return;

        var count = 0;
        for (var index = 0; index < _maps.Length; index++)
        {
            lock (_locks[index])
                count += _maps[index].Count;
        }

        Volatile.Write(ref _count, count);
        _countTrackingEnabled = true;
    }

    internal int Count => Volatile.Read(ref _count);

    public void Set(long key, TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            var map = _maps[stripe];
            if (!map.TryAdd(key, value))
            {
                map[key] = value;
                return;
            }

            if (_countTrackingEnabled)
                Interlocked.Increment(ref _count);
        }
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
            if (_countTrackingEnabled)
                Interlocked.Increment(ref _count);
            return created;
        }
    }

    public bool TryGetValue(long key, out TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
            return _maps[stripe].TryGetValue(key, out value!);
    }

    /// <summary>
    /// Captures an immutable projection while the entry remains protected by its stripe lock.
    /// This lets pooled values publish a generation-bound lease without a lookup-to-capture ABA gap.
    /// </summary>
    internal bool TryCapture<TSnapshot>(
        long key,
        Func<long, TValue, TSnapshot> capture,
        out TSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            if (_maps[stripe].TryGetValue(key, out var value))
            {
                snapshot = capture(key, value);
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryRemove(long key, out TValue value)
    {
        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            if (!_maps[stripe].TryGetValue(key, out value!) || !_maps[stripe].Remove(key))
                return false;
            if (_countTrackingEnabled)
                Interlocked.Decrement(ref _count);
            return true;
        }
    }

    public bool TryRemove(long key, TValue expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            if (!_maps[stripe].TryGetValue(key, out var existing) ||
                !ReferenceEquals(existing, expected) ||
                !_maps[stripe].Remove(key))
            {
                return false;
            }

            if (_countTrackingEnabled)
                Interlocked.Decrement(ref _count);
            return true;
        }
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

                var removed = _maps[i].Count;
                values.AddRange(_maps[i].Values);
                _maps[i].Clear();
                if (_countTrackingEnabled)
                    Interlocked.Add(ref _count, -removed);
            }
        }

        return values;
    }

    /// <summary>
    /// Copies a bounded per-stripe consistent view. Different stripes can advance between locks,
    /// while each projected entry remains stable for the duration of its capture callback.
    /// </summary>
    /// <param name="destination">A destination large enough for the map's configured upper bound.</param>
    /// <returns>The number of copied entries.</returns>
    internal int CopyEntries(Span<KeyValuePair<long, TValue>> destination)
        => CopyEntries(
            destination,
            static (key, value) => new KeyValuePair<long, TValue>(key, value));

    /// <summary>
    /// Copies immutable projections while each source entry remains protected by its stripe lock.
    /// The result is per-stripe consistent rather than one whole-map instant.
    /// </summary>
    internal int CopyEntries<TSnapshot>(
        Span<TSnapshot> destination,
        Func<long, TValue, TSnapshot> capture)
    {
        if (TryCopyEntries(destination, capture, out var count))
            return count;

        throw new ArgumentException(
            "The destination is smaller than the current map value count.",
            nameof(destination));
    }

    /// <summary>
    /// Attempts to copy immutable projections without using an exception for a sizing race.
    /// <paramref name="count"/> reports how many destination elements were written even when the
    /// destination becomes too small at a later stripe.
    /// </summary>
    internal bool TryCopyEntries<TSnapshot>(
        Span<TSnapshot> destination,
        Func<long, TValue, TSnapshot> capture,
        out int count)
    {
        ArgumentNullException.ThrowIfNull(capture);
        count = 0;
        for (var index = 0; index < _maps.Length; index++)
        {
            lock (_locks[index])
            {
                var map = _maps[index];
                if (map.Count > destination.Length - count)
                    return false;
                foreach (var entry in map)
                    destination[count++] = capture(entry.Key, entry.Value);
            }
        }
        return true;
    }

    private int GetStripe(long key)
    {
        var hash = unchecked((int)(key ^ (key >> 32)));
        hash &= int.MaxValue;
        return hash & _stripeMask;
    }
}
