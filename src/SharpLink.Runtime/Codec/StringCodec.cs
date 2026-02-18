namespace SharpLink.Runtime;

internal sealed class StringCodec : IRpcCodec<string?>
{
    internal static readonly StringCodec Instance = new();
    private const int CharSize = 2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in string? value, in ArrayBufferWriter<byte> writer)
    {
        if (value is null)
        {
            CodecHelpers.WriteInt32(writer, -1);
            return;
        }

        if (value.Length == 0)
        {
            CodecHelpers.WriteInt32(writer, 0);
            return;
        }
        
        var bytesCount = value.Length * CharSize;

        var span = writer.GetSpan(bytesCount + 4);

        BinaryPrimitives.WriteInt32LittleEndian(span[..4], bytesCount);
        
        value.AsSpan().CopyTo(MemoryMarshal.Cast<byte, char>(span[4..]));
        
        writer.Advance(bytesCount + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadLittleEndian(out int bytesCount) || bytesCount < 0)
            return null;

        if (bytesCount == 0) return string.Empty;

        if (reader.UnreadSpan.Length >= bytesCount)
        {
            var charSpan = MemoryMarshal.Cast<byte, char>(reader.UnreadSpan[..bytesCount]);
            var res = new string(charSpan);
            reader.Advance(bytesCount);
            return res;
        }
        
        var sequenceSlice = reader.UnreadSpan[..bytesCount];

        var result = string.Create(bytesCount / CharSize, sequenceSlice, static (destSpan, sequence) =>
        {
            sequence.CopyTo(MemoryMarshal.AsBytes(destSpan));
        });
        
        reader.Advance(bytesCount);
        return result;
    }
}