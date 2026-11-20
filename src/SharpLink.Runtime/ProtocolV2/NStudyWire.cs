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

internal static class NStudyWire
{
    internal const ushort Minor = 0x79A0;
    internal const ProtocolV2FrameType Bind = (ProtocolV2FrameType)244;
    internal const ProtocolV2FrameType BindAck = (ProtocolV2FrameType)245;
    internal const int BindingLimit = 64;
    private const int BindBytes = 48;
    private const int AckBytes = 112;

    internal static bool IsBindingFrame(ProtocolV2FrameType type) => type is Bind or BindAck;

    internal static void ValidateLength(ProtocolV2FrameType type, int size)
    {
        if (size != (type == Bind ? BindBytes : AckBytes))
            throw Invalid("Incorrect private Bind frame length.");
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
            ? (ack.BindingId >> 32) != 1 || (uint)ack.BindingId is 0 or > BindingLimit || ack.Target == default
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
