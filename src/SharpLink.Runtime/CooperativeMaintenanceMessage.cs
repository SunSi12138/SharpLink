namespace SharpLink.Runtime;

// A bounded connection-control envelope, never attached to ordinary RPCs. All identities are
// exact incarnations. A peer cannot replace an operation, digest or session by replaying an ACK.
internal enum CooperativeMaintenanceStage : byte
{
    Welcome = 1,
    Enroll,
    Enrolled,
    Freeze,
    IngressFence,
    ReturnFence,
    Quiescent,
    Commit,
    Committed,
    Resume,
    Resumed
}

internal readonly record struct MaintenanceDigest(ulong A, ulong B, ulong C, ulong D)
{
    internal static MaintenanceDigest Read(ReadOnlySpan<byte> bytes)
        => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));

    internal void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[32];
        Write(bytes);
        return Convert.ToHexString(bytes);
    }
}

internal readonly record struct CooperativeMaintenanceMessage(
    CooperativeMaintenanceStage Stage,
    Guid Domain,
    Guid Server,
    Guid Participant,
    Guid Runtime,
    Guid Session,
    Guid Operation,
    MaintenanceDigest Digest,
    int SessionCount,
    long Generation)
{
    internal const int PayloadBytes = 144;
    internal const int MaximumSessions = 4096;

    internal void Write(IRpcByteBufferWriter writer)
    {
        Validate();
        var span = writer.GetSpan(PayloadBytes)[..PayloadBytes];
        span.Clear();
        span[0] = 1;
        span[1] = (byte)Stage;
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], SessionCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], Generation);
        Domain.TryWriteBytes(span[16..32]);
        Server.TryWriteBytes(span[32..48]);
        Participant.TryWriteBytes(span[48..64]);
        Runtime.TryWriteBytes(span[64..80]);
        Session.TryWriteBytes(span[80..96]);
        Operation.TryWriteBytes(span[96..112]);
        Digest.Write(span[112..144]);
        writer.Advance(PayloadBytes);
    }

    internal static CooperativeMaintenanceMessage Read(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != PayloadBytes)
            throw ProtocolV2FrameParser.Violation("Maintenance envelope length is invalid.");
        Span<byte> bytes = stackalloc byte[PayloadBytes];
        payload.CopyTo(bytes);
        if (bytes[0] != 1 || bytes[2] != 0 || bytes[3] != 0)
            throw ProtocolV2FrameParser.Violation("Maintenance version or reserved fields are invalid.");
        var message = new CooperativeMaintenanceMessage(
            (CooperativeMaintenanceStage)bytes[1],
            new Guid(bytes[16..32]), new Guid(bytes[32..48]),
            new Guid(bytes[48..64]), new Guid(bytes[64..80]),
            new Guid(bytes[80..96]), new Guid(bytes[96..112]),
            MaintenanceDigest.Read(bytes[112..144]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        message.Validate();
        return message;
    }

    internal bool MatchesOperation(in CooperativeMaintenanceMessage other)
        => Domain == other.Domain && Server == other.Server &&
           Participant == other.Participant && Runtime == other.Runtime &&
           Session == other.Session && Operation == other.Operation &&
           Digest == other.Digest && SessionCount == other.SessionCount &&
           Generation == other.Generation;

    private void Validate()
    {
        if (Stage is < CooperativeMaintenanceStage.Welcome or > CooperativeMaintenanceStage.Resumed ||
            Domain == Guid.Empty || Server == Guid.Empty || Generation < 0 ||
            SessionCount < 0 || SessionCount > MaximumSessions)
            throw ProtocolV2FrameParser.Violation("Maintenance envelope fields are invalid.");
        if (Stage == CooperativeMaintenanceStage.Welcome)
        {
            if (Participant != Guid.Empty || Runtime != Guid.Empty || Session != Guid.Empty)
                throw ProtocolV2FrameParser.Violation("Maintenance welcome contains peer-supplied identity.");
        }
        else if (Participant == Guid.Empty || Runtime == Guid.Empty || Session == Guid.Empty)
            throw ProtocolV2FrameParser.Violation("Maintenance participant identity is missing.");
        if (Stage <= CooperativeMaintenanceStage.Enrolled)
        {
            if (Operation != Guid.Empty || Digest != default || SessionCount != 0 || Generation != 0)
                throw ProtocolV2FrameParser.Violation("Maintenance enrollment contains operation fields.");
        }
        else if (Operation == Guid.Empty || Digest == default || SessionCount == 0 || Generation == 0)
            throw ProtocolV2FrameParser.Violation("Maintenance operation identity is missing.");
    }
}
