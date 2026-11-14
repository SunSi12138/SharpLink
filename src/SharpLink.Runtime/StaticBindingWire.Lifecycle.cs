namespace SharpLink.Runtime;

internal static partial class StaticBindingWire
{
    internal const ProtocolV2FrameType Invalidate = ProtocolV2FrameType.BindingInvalidated;
    internal const ProtocolV2FrameType Release = ProtocolV2FrameType.BindingRelease;

    internal static ulong[] ReadInvalidation(ReadOnlySequence<byte> payload)
    {
        if (payload.Length < 8 || payload.Length > 8 + Capacity * 8)
            throw Malformed("Invalid binding invalidation length.");
        var reader = new SequenceReader<byte>(payload);
        _ = reader.TryReadLittleEndian(out long count);
        if (count is < 1 or > Capacity || payload.Length != 8 + count * 8)
            throw Malformed("Invalid binding invalidation count.");
        var tokens = new ulong[(int)count];
        for (var i = 0; i < tokens.Length; i++)
        {
            _ = reader.TryReadLittleEndian(out long raw);
            tokens[i] = unchecked((ulong)raw);
            if ((uint)tokens[i] is 0 or > Capacity || (uint)(tokens[i] >> 32) == 0)
                throw Malformed("Invalid binding token.");
        }
        return tokens;
    }

    internal static void SendInvalidation(RpcSession session, ulong[] tokens)
    {
        if (tokens.Length == 0) return;
        if (tokens.Length > Capacity) throw new ArgumentOutOfRangeException(nameof(tokens));
        var writer = session.RentFrameWriter();
        try
        {
            using (writer.BeginPacketScope(Invalidate, ProtocolV2FrameFlags.None, 0))
            {
                var span = writer.GetSpan(8 + tokens.Length * 8);
                BinaryPrimitives.WriteInt64LittleEndian(span, tokens.Length);
                for (var i = 0; i < tokens.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(span[(8 + i * 8)..], tokens[i]);
                writer.Advance(8 + tokens.Length * 8);
            }
        }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
        session.SendPacket(writer);
    }

    internal static ulong ReadRelease(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != 8) throw Malformed("Binding release must contain one full token.");
        var reader = new SequenceReader<byte>(payload);
        _ = reader.TryReadLittleEndian(out long raw);
        return unchecked((ulong)raw);
    }

    internal static ValueTask SendReleaseAsync(RpcSession session, ulong token, CancellationToken cancellationToken)
    {
        var writer = session.RentFrameWriter();
        try
        {
            using (writer.BeginPacketScope(Release, ProtocolV2FrameFlags.None, 0))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(8), token);
                writer.Advance(8);
            }
        }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
        return session.SendPacketWithBackpressureAsync(writer, cancellationToken);
    }
}

