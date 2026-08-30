namespace SharpLink.Runtime;

/// <summary>Provides the deterministic fixed-width native Codec for one enum payload type.</summary>
internal sealed class EnumCodec<T> : IRpcCodec<T>
{
    internal static readonly EnumCodec<T> Instance = new();
    private static readonly int Size = ValidateSize();

    private EnumCodec()
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in T value, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ref var destination = ref MemoryMarshal.GetReference(writer.GetSpan(Size));
        Unsafe.WriteUnaligned(ref destination, value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buffer.FirstSpan));

        Span<byte> temporary = stackalloc byte[sizeof(ulong)];
        var target = temporary[..Size];
        buffer.CopyTo(target);
        return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(target));
    }

    private static int ValidateSize()
    {
        if (!typeof(T).IsEnum)
            throw new InvalidOperationException($"'{typeof(T).FullName}' is not an enum Codec target.");
        var size = Unsafe.SizeOf<T>();
        return size is 1 or 2 or 4 or 8
            ? size
            : throw new InvalidOperationException($"Enum '{typeof(T).FullName}' has unsupported size {size}.");
    }
}
