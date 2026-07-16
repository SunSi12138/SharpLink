namespace SharpLink.Runtime;

internal sealed class CharCodec : IRpcCodec<char>
{
    internal static readonly CharCodec Instance = new();
    private const int Size = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in char value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public char Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size) 
        {
            return Unsafe.ReadUnaligned<char>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        }
        
        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        return Unsafe.ReadUnaligned<char>(ref MemoryMarshal.GetReference(temp));
    }
}

internal sealed class NullableCharCodec : IRpcCodec<char?>
{
    internal static readonly NullableCharCodec Instance = new();
    private const int Size = 3; // 1 byte Tag + 2 bytes Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in char? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1; // Tag
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref start, 1), 
                value.GetValueOrDefault()
            );
        }
        else
        {
            start = 0; // Tag
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref start, 1), 
                (ushort)0
            );
        }
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public char? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.ReadUnaligned<char>(ref Unsafe.Add(ref start, 1));
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (tempStart == 0) return null;
        
        return Unsafe.ReadUnaligned<char>(ref Unsafe.Add(ref tempStart, 1));
    }
}
