namespace SharpLink.Runtime;

internal sealed class GuidCodec : IRpcCodec<Guid>
{
    internal static readonly GuidCodec Instance = new();
    private const int Size = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Guid value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Guid Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size) 
            return Unsafe.ReadUnaligned<Guid>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
            
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<Guid>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableGuidCodec : IRpcCodec<Guid?>
{
    internal static readonly NullableGuidCodec Instance = new();
    private const int Size = 17; // 1 byte Tag + 16 bytes Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Guid? value, IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(Size);
        if (value.HasValue)
        {
            span[0] = 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), 1), value.GetValueOrDefault());
        }
        else
        {
            span.Clear();
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Guid? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<Guid>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);

        if (temp[0] == 0) return null;
        return Unsafe.ReadUnaligned<Guid>(ref Unsafe.Add(ref MemoryMarshal.GetReference(temp), 1));
    }
}
