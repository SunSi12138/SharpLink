namespace SharpLink.Runtime;

/// <summary>
/// Stores the usual one or two child routes without hashing. Callers provide synchronization.
/// Promotion retains Dictionary slot order, including its reuse of removed entry positions.
/// </summary>
internal sealed class SmallStreamRouteTable<TValue> where TValue : class
{
    private TValue? _first;
    private TValue? _second;
    private Dictionary<ushort, TValue>? _fallback;
    private ushort _firstId;
    private ushort _secondId;
    private byte _used;
    private sbyte _freeHead = -1;
    private sbyte _nextFree = -1;

    internal int Count => _fallback?.Count ?? ((_first is null ? 0 : 1) + (_second is null ? 0 : 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(ushort id, out TValue value)
    {
        if (_fallback is not null)
            return _fallback.TryGetValue(id, out value!);
        value = _first is not null && id == _firstId ? _first : id == _secondId ? _second! : null!;
        return value is not null;
    }

    internal bool ContainsKey(ushort id) => TryGetValue(id, out _);

    internal void Add(ushort id, TValue value)
    {
        if (_fallback is not null)
        {
            _fallback.Add(id, value);
            return;
        }
        if (ContainsKey(id))
            throw new ArgumentException("A route with this stream ID is already registered.", nameof(id));
        int index;
        if (_freeHead >= 0)
        {
            index = _freeHead;
            _freeHead = _nextFree;
            _nextFree = -1;
        }
        else if (_used < 2)
        {
            index = _used++;
        }
        else
        {
            Promote().Add(id, value);
            return;
        }
        if (index == 0)
        {
            _firstId = id;
            _first = value;
        }
        else
        {
            _secondId = id;
            _second = value;
        }
    }

    internal bool Remove(ushort id, out TValue value)
    {
        if (_fallback is not null)
            return _fallback.Remove(id, out value!);
        if (!TryGetValue(id, out value))
            return false;
        var index = _first is not null && id == _firstId ? 0 : 1;
        if (index == 0)
            _first = null;
        else
            _second = null;
        // Dictionary reuses the last removed physical position first. At most two
        // positions exist here; promotion hands all subsequent bookkeeping to it.
        _nextFree = _freeHead;
        _freeHead = (sbyte)index;
        return true;
    }

    internal void Clear()
    {
        _first = null;
        _second = null;
        _used = 0;
        _freeHead = -1;
        _nextFree = -1;
        _fallback?.Clear();
    }

    internal TValue[] GetValuesSnapshot()
    {
        if (_fallback is not null)
            return [.. _fallback.Values];
        if (_first is null)
            return _second is null ? [] : [_second];
        return _second is null ? [_first] : [_first, _second];
    }

    public Enumerator GetEnumerator() => new(this);

    private Dictionary<ushort, TValue> Promote()
    {
        var fallback = new Dictionary<ushort, TValue>(3);
        if (_first is not null)
            fallback.Add(_firstId, _first);
        if (_second is not null)
            fallback.Add(_secondId, _second);
        _first = null;
        _second = null;
        return _fallback = fallback;
    }

    internal struct Enumerator
    {
        private readonly SmallStreamRouteTable<TValue> _owner;
        private Dictionary<ushort, TValue>.Enumerator _fallback;
        private int _index;

        internal Enumerator(SmallStreamRouteTable<TValue> owner)
        {
            _owner = owner;
            _fallback = owner._fallback?.GetEnumerator() ?? default;
            _index = -1;
        }

        public KeyValuePair<ushort, TValue> Current => _owner._fallback is not null
            ? _fallback.Current
            : _index == 0
                ? new(_owner._firstId, _owner._first!)
                : new(_owner._secondId, _owner._second!);

        public bool MoveNext()
        {
            if (_owner._fallback is not null)
                return _fallback.MoveNext();
            while (++_index < 2)
            {
                if (_index == 0 ? _owner._first is not null : _owner._second is not null)
                    return true;
            }
            return false;
        }
    }
}
