namespace SharpLink.Runtime;

internal sealed class ByteCodec : IRpcCodec<byte>
{
    internal static readonly ByteCodec Instance = new();
    private const int Size = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in byte value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        return CodecHelpers.ReadUnmanaged<byte>(buffer);
    }
}

internal sealed class NullableByteCodec : IRpcCodec<byte?>
{
    internal static readonly NullableByteCodec Instance = new();
    private const int Size = 2; // 1 byte tag + 1 byte value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in byte? value, IBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            Unsafe.Add(ref start, 1) = value.GetValueOrDefault();
        }
        else
        {
            Unsafe.WriteUnaligned(ref start, (ushort)0);
        }

        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureExactSize(buffer, Size);
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (!CodecHelpers.ReadNullablePresence(ref start, Size - 1)) return null;
            return Unsafe.Add(ref start, 1);
        }

        Span<byte> temp = stackalloc byte[Size];

        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (!CodecHelpers.ReadNullablePresence(ref tempStart, Size - 1)) return null;

        return Unsafe.Add(ref tempStart, 1);
    }
}
