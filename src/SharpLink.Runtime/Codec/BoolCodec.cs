namespace SharpLink.Runtime;

internal sealed class BoolCodec : IRpcCodec<bool>
{
    internal static readonly BoolCodec Instance = new();
    private const int Size = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in bool value, IBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned( ref MemoryMarshal.GetReference(writer.GetSpan(Size)), Unsafe.As<bool, byte>(ref Unsafe.AsRef(in value)));
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        return CodecHelpers.ReadUnmanaged<byte>(buffer) != 0;
    }
}

internal sealed class NullableBoolCodec : IRpcCodec<bool?>
{
    internal static readonly NullableBoolCodec Instance = new();
    private const int Size = 1;
    
    // 0xFF (255) = Null
    // 0 = False
    // 1 = True
    private const byte NullTag = 0xFF; 

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in bool? value, IBufferWriter<byte> writer)
    {
        ref var dest = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            var val = value.GetValueOrDefault();
            dest = Unsafe.As<bool, byte>(ref Unsafe.AsRef(in val));
        }
        else
        {
            dest = NullTag;
        }

        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        CodecHelpers.EnsureAvailable(buffer, Size);
        var val = CodecHelpers.ReadUnmanaged<byte>(buffer);
        if (val == NullTag) return null;
        return val != 0;
    }
}
