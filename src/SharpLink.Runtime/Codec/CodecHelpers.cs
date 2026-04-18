namespace SharpLink.Runtime;

internal static class CodecHelpers
{
    private const int Size = 4;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32(in ArrayBufferWriter<byte> writer, in int value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        writer.Advance(Size);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitmapLen(int count) => (count + 7) >> 3;

    public static void Serialize(in ReadOnlySpan<short?> src, in ArrayBufferWriter<byte> writer)
    {
        var count = src.Length;
        WriteInt32(writer, count);
        if (count == 0) return;

        // pass1: count non-null
        var nonNull = 0;
        for (var i = 0; i < count; i++)
            if (src[i].HasValue) nonNull++;

        var bmLen = BitmapLen(count);
        var payloadLen = checked(bmLen + nonNull * 2);

        var dst = writer.GetSpan(payloadLen);
        dst[..bmLen].Clear();

        var valueOffset = bmLen;
        for (var i = 0; i < count; i++)
        {
            var v = src[i];
            if (!v.HasValue) continue;

            dst[i >> 3] |= (byte)(1 << (i & 7));
            BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(valueOffset, 2), v.Value);
            valueOffset += 2;
        }

        writer.Advance(payloadLen);
    }
}