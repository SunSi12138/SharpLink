namespace SharpLink.Runtime;

// Private test grammar. Ordinary requests retain a 16-byte prefix but the
// first eight bytes are a Session BindingId, never a ContractId fallback.
internal readonly record struct NStudyAuthority(Guid Server, Guid RegistryEpoch, Guid PhysicalSession);
internal readonly record struct NStudyTarget(Guid Registration, long Publication);
internal readonly record struct NStudyBind(long Contract, RpcHash128 Hash, NStudyTarget Expected);
internal enum NStudyBindStatus : long { Bound, Missing, Incompatible, Superseded, Full, Unavailable }
internal readonly record struct NStudyAck(
    NStudyBindStatus Status, long Contract, RpcHash128 Hash, NStudyAuthority Authority,
    long BindingId, NStudyTarget Target);

internal static partial class NStudyWire
{
    internal const ushort Minor = ProtocolV2Constants.MinorVersion;
    internal const ProtocolV2FrameType Bind = (ProtocolV2FrameType)244;
    internal const ProtocolV2FrameType BindAck = (ProtocolV2FrameType)245;
    internal const ProtocolV2FrameType Release = (ProtocolV2FrameType)246;
    internal const ProtocolV2FrameType Invalidated = (ProtocolV2FrameType)247;
    internal const ProtocolV2FrameType OneWayInputRequest = (ProtocolV2FrameType)249;
    internal const int BindingLimit = 64;
    internal const int OneWayInputLimit = 127;
    private const int BindBytes = 48;
    private const int AckBytes = 112;

    internal static bool IsBindingFrame(ProtocolV2FrameType type) =>
        type is Bind or BindAck or Release or Invalidated or Coordination or InventoryQuery or InventoryReply
            or TargetDeclaration or TargetDeclarationReply;

    internal static bool IsRequestFrame(ProtocolV2FrameType type) =>
        type is ProtocolV2FrameType.Request or OneWayInputRequest;

    internal static int ReadOneWayInputCount(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var count) || count is 0 or > OneWayInputLimit)
            throw Invalid("Private OneWay input stream count is outside its bound.");
        return count;
    }

    internal static void ValidateLength(ProtocolV2FrameType type, int size)
    {
        var valid = type switch
        {
            Bind => size == BindBytes,
            BindAck => size == AckBytes,
            Coordination => size == CoordinationBytes,
            InventoryQuery => size is 0 or InventoryAuthorityBytes,
            InventoryReply => IsInventoryLength(size),
            TargetDeclaration => size == TargetDeclarationBytes,
            TargetDeclarationReply => size == TargetDeclarationReplyBytes,
            Invalidated => size is >= sizeof(long) and <= BindingLimit * sizeof(long) &&
                size % sizeof(long) == 0,
            _ => size == sizeof(long)
        };
        if (!valid)
            throw Invalid("Incorrect private Bind frame length.");
    }

    internal static bool IsBindingId(long token) =>
        (ulong)token >> 32 != 0 && (uint)token is > 0 and <= BindingLimit;

    internal static int SlotIndex(long token) => unchecked((int)(uint)token - 1);
    internal static uint Incarnation(long token) => (uint)((ulong)token >> 32);
    internal static long BindingId(int index, uint incarnation) =>
        unchecked((long)(((ulong)incarnation << 32) | (uint)(index + 1)));

    internal static int ReadInvalidated(ReadOnlySequence<byte> payload, Span<long> tokens)
    {
        ValidateLength(Invalidated, checked((int)payload.Length));
        var count = (int)payload.Length / sizeof(long);
        var reader = new SequenceReader<byte>(payload);
        ulong seen = 0;
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadLittleEndian(out long token) || !IsBindingId(token))
                throw Invalid("Invalid private retirement identity.");
            var slot = 1UL << SlotIndex(token);
            if ((seen & slot) != 0)
                throw Invalid("Duplicate slot in private retirement batch.");
            seen |= slot;
            tokens[i] = token;
        }
        return count;
    }

    internal static void WriteInvalidated(IBufferWriter<byte> writer, ReadOnlySpan<long> tokens)
    {
        var size = tokens.Length * sizeof(long);
        ValidateLength(Invalidated, size);
        var bytes = writer.GetSpan(size)[..size];
        foreach (var token in tokens)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, token);
            bytes = bytes[sizeof(long)..];
        }
        writer.Advance(size);
    }

    internal static long ReadRelease(ReadOnlySequence<byte> payload)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadFixed(payload, bytes);
        var token = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (!IsBindingId(token))
            throw Invalid("Invalid private Release binding identity.");
        return token;
    }

    internal static void WriteRelease(IBufferWriter<byte> writer, long token)
    {
        if (!IsBindingId(token))
            throw Invalid("Invalid private Release binding identity.");
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), token);
        writer.Advance(sizeof(long));
    }

    internal static void WriteBind(IBufferWriter<byte> output, in NStudyBind bind)
    {
        ValidateContract(bind.Contract, bind.Hash);
        ValidateTarget(bind.Expected);
        var bytes = output.GetSpan(BindBytes)[..BindBytes];
        WriteContract(bytes, bind.Contract, bind.Hash);
        WriteTarget(bytes[24..], bind.Expected);
        output.Advance(BindBytes);
    }

    internal static NStudyBind ReadBind(ReadOnlySequence<byte> input)
    {
        Span<byte> bytes = stackalloc byte[BindBytes];
        ReadFixed(input, bytes);
        var (cid, hash) = ReadContract(bytes);
        var expected = ReadTarget(bytes[24..]);
        return new(cid, hash, expected);
    }

    internal static void WriteAck(IBufferWriter<byte> output, in NStudyAck ack)
    {
        ValidateAck(ack);
        var bytes = output.GetSpan(AckBytes)[..AckBytes];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, (long)ack.Status);
        WriteContract(bytes[8..], ack.Contract, ack.Hash);
        _ = ack.Authority.Server.TryWriteBytes(bytes[32..48]);
        _ = ack.Authority.RegistryEpoch.TryWriteBytes(bytes[48..64]);
        _ = ack.Authority.PhysicalSession.TryWriteBytes(bytes[64..80]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[80..], ack.BindingId);
        WriteTarget(bytes[88..], ack.Target);
        output.Advance(AckBytes);
    }

    internal static NStudyAck ReadAck(ReadOnlySequence<byte> input)
    {
        Span<byte> bytes = stackalloc byte[AckBytes];
        ReadFixed(input, bytes);
        var (cid, hash) = ReadContract(bytes[8..]);
        var ack = new NStudyAck((NStudyBindStatus)BinaryPrimitives.ReadInt64LittleEndian(bytes),
            cid, hash, new(new Guid(bytes[32..48]), new Guid(bytes[48..64]), new Guid(bytes[64..80])),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[80..]), ReadTarget(bytes[88..]));
        ValidateAck(ack);
        return ack;
    }

    private static void ValidateAck(in NStudyAck ack)
    {
        ValidateContract(ack.Contract, ack.Hash);
        ValidateTarget(ack.Target);
        if (ack.Authority.Server == Guid.Empty || ack.Authority.RegistryEpoch == Guid.Empty ||
            ack.Authority.PhysicalSession == Guid.Empty ||
            ack.Status is < NStudyBindStatus.Bound or > NStudyBindStatus.Unavailable)
            throw Invalid("Invalid private Bind acknowledgement identity.");
        if (ack.Status == NStudyBindStatus.Bound
            ? !IsBindingId(ack.BindingId) || ack.Target == default
            : ack.BindingId != 0 || ack.Target != default)
            throw Invalid("Invalid private Bind acknowledgement result.");
    }

    private static (long Contract, RpcHash128 Hash) ReadContract(ReadOnlySpan<byte> bytes)
    {
        var cid = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var hash = new RpcHash128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        ValidateContract(cid, hash);
        return (cid, hash);
    }

    private static void WriteContract(Span<byte> bytes, long cid, RpcHash128 hash)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes, cid);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], hash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], hash.Low);
    }

    private static void ValidateContract(long cid, RpcHash128 hash)
    {
        if (cid == 0 || hash.IsEmpty)
            throw Invalid("A private Bind requires a nonempty contract identity.");
    }

    private static void WriteTarget(Span<byte> bytes, NStudyTarget target)
    {
        _ = target.Registration.TryWriteBytes(bytes[..16]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], target.Publication);
    }

    private static NStudyTarget ReadTarget(ReadOnlySpan<byte> bytes)
    {
        var target = new NStudyTarget(new Guid(bytes[..16]), BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]));
        ValidateTarget(target);
        return target;
    }

    private static void ValidateTarget(NStudyTarget target)
    {
        if (target != default && (target.Registration == Guid.Empty || target.Publication <= 0))
            throw Invalid("Invalid exact registration in private Bind.");
    }

    private static void ReadFixed(ReadOnlySequence<byte> source, Span<byte> bytes)
    {
        if (source.Length != bytes.Length)
            throw Invalid("Incorrect private Bind payload length.");
        source.CopyTo(bytes);
    }

    private static SharpLinkException Invalid(string message) => ProtocolV2FrameParser.Violation(message);
}
