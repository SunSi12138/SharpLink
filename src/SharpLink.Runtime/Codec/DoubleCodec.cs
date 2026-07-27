namespace SharpLink.Runtime;

internal sealed class DoubleCodec : IRpcCodec<double>
{
    internal static readonly DoubleCodec Instance = new();
    private const int Size = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in double value, IBufferWriter<byte> writer) { Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value); writer.Advance(Size); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size) 
            return Unsafe.ReadUnaligned<double>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<double>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableDoubleCodec : IRpcCodec<double?>
{
    internal static readonly NullableDoubleCodec Instance = new();
    private const int Size = 9; // 1 byte Tag + 8 bytes Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in double? value, IBufferWriter<byte> writer)
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
    public double? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return Unsafe.ReadUnaligned<double>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        
        if (!CodecHelpers.ReadNullablePresence(ref MemoryMarshal.GetReference(temp), Size - 1)) return null;
        return Unsafe.ReadUnaligned<double>(ref Unsafe.Add(ref MemoryMarshal.GetReference(temp), 1));
    }
}
