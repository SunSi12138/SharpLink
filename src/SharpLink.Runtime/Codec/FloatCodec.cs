namespace SharpLink.Runtime;

internal sealed class FloatCodec : IRpcCodec<float>
{
    internal static readonly FloatCodec Instance = new();
    private const int Size = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in float value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size) 
            return Unsafe.ReadUnaligned<float>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
            
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<float>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableFloatCodec : IRpcCodec<float?>
{
    internal static readonly NullableFloatCodec Instance = new();
    private const int Size = 5; // 1 byte Tag + 4 bytes Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in float? value, IBufferWriter<byte> writer)
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
    public float? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        
        if (!CodecHelpers.ReadNullablePresence(ref MemoryMarshal.GetReference(temp), Size - 1)) return null;
        return Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref MemoryMarshal.GetReference(temp), 1));
    }
}
