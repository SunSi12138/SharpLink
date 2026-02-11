namespace SharpLink.Generator;

// 解决 Record 中数组比较导致缓存失效的问题
internal readonly struct EquatableArray<T>(ImmutableArray<T> array) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    private readonly ImmutableArray<T> _array = array;

    public T this[int index] => _array[index];
    public int Length => _array.Length;
    public IEnumerator<T> GetEnumerator() => (_array.IsDefault ? Enumerable.Empty<T>() : _array).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableArray<T> other) =>
        (_array.IsDefault && other._array.IsDefault) ||
        (!_array.IsDefault && !other._array.IsDefault && _array.SequenceEqual(other._array));

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);
    public override int GetHashCode()
    {
        if (_array.IsDefaultOrEmpty) return 0;
        var hashCode = 0;
        foreach (var item in _array) hashCode = (hashCode * 397) ^ (item?.GetHashCode() ?? 0);
        return hashCode;
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array) => new(array);
    public static implicit operator ImmutableArray<T>(EquatableArray<T> array) => array._array;
}
