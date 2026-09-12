namespace SharpLink.Runtime;

public static partial class ProtocolV2PayloadCodec
{
    private const int SessionRefreshRequestedBytes = 16 + sizeof(ulong);

    /// <summary>Writes a bounded server-instance-scoped session refresh request.</summary>
    public static void WriteSessionRefreshRequested(
        IBufferWriter<byte> writer,
        in ProtocolV2SessionRefreshRequested request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (request.ServerInstanceId == Guid.Empty)
            throw new ArgumentException("A non-empty server instance ID is required.", nameof(request));
        if (request.DesiredGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Desired generation must be non-zero.");

        var span = writer.GetSpan(SessionRefreshRequestedBytes);
        if (!request.ServerInstanceId.TryWriteBytes(span[..16]))
            throw new InvalidOperationException("Failed to encode the server instance ID.");
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], request.DesiredGeneration);
        writer.Advance(SessionRefreshRequestedBytes);
    }

    /// <summary>Reads one complete server-instance-scoped session refresh request.</summary>
    public static ProtocolV2SessionRefreshRequested ReadSessionRefreshRequested(
        ReadOnlySequence<byte> payload)
    {
        if (payload.Length != SessionRefreshRequestedBytes)
            throw ProtocolV2FrameParser.Violation("SessionRefreshRequested payload must be exactly 24 bytes.");

        Span<byte> bytes = stackalloc byte[SessionRefreshRequestedBytes];
        payload.CopyTo(bytes);
        var serverInstanceId = new Guid(bytes[..16]);
        var desiredGeneration = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        if (serverInstanceId == Guid.Empty || desiredGeneration == 0)
            throw ProtocolV2FrameParser.Violation("SessionRefreshRequested contains an invalid authority or generation.");
        return new ProtocolV2SessionRefreshRequested(serverInstanceId, desiredGeneration);
    }
}
