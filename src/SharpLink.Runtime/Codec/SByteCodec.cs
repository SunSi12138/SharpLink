namespace SharpLink.Runtime;

internal sealed class SByteCodec : IRpcCodec<sbyte>
{
    internal static readonly SByteCodec Instance = new();
    private const int Size = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in sbyte value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        return CodecHelpers.ReadUnmanaged<sbyte>(buffer);
    }
}

internal sealed class NullableSByteCodec : IRpcCodec<sbyte?>
{
    internal static readonly NullableSByteCodec Instance = new();
    private const int Size = 2; // 1 Tag + 1 Value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in sbyte? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            Unsafe.Add(ref start, 1) = (byte)value.GetValueOrDefault();
        }
        else
        {
            Unsafe.WriteUnaligned(ref start, (ushort)0);
        }
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return (sbyte)Unsafe.Add(ref start, 1);
        }

        Span<byte> temp = stackalloc byte[Size];
        buffer.CopyTo(temp);
        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        
        if (!CodecHelpers.ReadNullablePresence(ref tempStart, Size - 1)) return null;
        return (sbyte)Unsafe.Add(ref tempStart, 1);
    }
}
