namespace SharpLink.Runtime;

internal sealed class StripedLongMap<TValue> where TValue : class
{
    private readonly Lock[] _locks;
    private readonly Dictionary<long, TValue>[] _maps;
    private readonly int _stripeMask;

    public StripedLongMap() : this(new RuntimeConcurrencyOptions())
    {
    }

    public StripedLongMap(RuntimeConcurrencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stripeCount = options.StripeCount;
        var initialMapCapacityPerStripe = options.InitialMapCapacityPerStripe;
        RuntimeConcurrencyOptions.Validate(stripeCount, initialMapCapacityPerStripe);

        _locks = new Lock[stripeCount];
        _maps = new Dictionary<long, TValue>[stripeCount];
        _stripeMask = stripeCount - 1;

        for (var i = 0; i < stripeCount; i++)
        {
            _locks[i] = new Lock();
            _maps[i] = initialMapCapacityPerStripe == 0
                ? []
                : new Dictionary<long, TValue>(initialMapCapacityPerStripe);
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
            return _maps[stripe].TryGetValue(key, out value!) && _maps[stripe].Remove(key);
    }

    public bool TryRemove(long key, TValue expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var stripe = GetStripe(key);
        lock (_locks[stripe])
        {
            return _maps[stripe].TryGetValue(key, out var existing) &&
                   ReferenceEquals(existing, expected) &&
                   _maps[stripe].Remove(key);
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

                values.AddRange(_maps[i].Values);
                _maps[i].Clear();
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
        ArgumentNullException.ThrowIfNull(capture);
        var count = 0;
        for (var index = 0; index < _maps.Length; index++)
        {
            lock (_locks[index])
            {
                var map = _maps[index];
                if (map.Count > destination.Length - count)
                {
                    throw new ArgumentException(
                        "The destination is smaller than the current map value count.",
                        nameof(destination));
                }
                foreach (var entry in map)
                    destination[count++] = capture(entry.Key, entry.Value);
            }
        }
        return count;
    }

    private int GetStripe(long key)
    {
        var hash = unchecked((int)(key ^ (key >> 32)));
        hash &= int.MaxValue;
        return hash & _stripeMask;
    }
}
