namespace SharpLink.Runtime;

internal sealed class BoolCodec : IRpcCodec<bool>
{
    internal static readonly BoolCodec Instance = new();
    private const int Size = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in bool value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned( ref MemoryMarshal.GetReference(writer.GetSpan(Size)), Unsafe.As<bool, byte>(ref Unsafe.AsRef(in value)));
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Deserialize(in ReadOnlySequence<byte> buffer)
    {
        return MemoryMarshal.GetReference(buffer.FirstSpan) != 0;
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
    public void Serialize(in bool? value, in ArrayBufferWriter<byte> writer)
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
        var val = MemoryMarshal.GetReference(buffer.FirstSpan);
        if (val == NullTag) return null;
        return val != 0;
    }
}