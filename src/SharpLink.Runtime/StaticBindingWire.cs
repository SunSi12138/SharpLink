namespace SharpLink.Runtime;

// Bounded Session-local contract binding control protocol. These are not ordinary RPC methods.
internal static partial class StaticBindingWire
{
    internal const ProtocolV2FrameType Bind = ProtocolV2FrameType.BindContract;
    internal const ProtocolV2FrameType Ack = ProtocolV2FrameType.BindAck;
    internal const int RequestBytes = 40;
    internal const int ReceiptRequestBytes = 112;
    internal const int AckBytes = 40;
    internal const int Capacity = 64;
    internal const int MaxMethods = 256;
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    internal static SharpLinkException Error(StaticBindingFailure failure)
        => new(failure switch
        {
            StaticBindingFailure.Capacity => SharpLinkErrorCode.ResourceExhausted,
            StaticBindingFailure.NoBoundRoute or StaticBindingFailure.Unavailable or StaticBindingFailure.Retired => SharpLinkErrorCode.Unavailable,
            StaticBindingFailure.UnknownMethod => SharpLinkErrorCode.Unimplemented,
            _ => SharpLinkErrorCode.FailedPrecondition
        }, (ushort)(60000 + (ushort)failure), $"Contract binding rejected: {failure}.");

    internal static SharpLinkProtocolViolationException Malformed(string message)
        => new(ProtocolViolationReason.MalformedFrame, message);

    internal static StaticBindingRequest ReadRequest(ReadOnlySequence<byte> payload)
    {
        if (payload.Length is not RequestBytes and not ReceiptRequestBytes)
            throw Malformed("BindContract has an invalid bounded request length.");
        Span<byte> bytes = stackalloc byte[ReceiptRequestBytes];
        payload.CopyTo(bytes);
        var contract = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var hash = new RpcHash128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        var requirement = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        var present = BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]);
        if (requirement is < 0 or > 2 || present is < 0 or > 1 ||
            payload.Length != (present == 1 ? ReceiptRequestBytes : RequestBytes))
            throw Malformed("BindContract constraint header is invalid.");
        RpcRegistrationReceipt? receipt = present == 0 ? null : new(new Guid(bytes[40..56]), new Guid(bytes[56..72]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[80..]),
            new RpcHash128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..])),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[104..]));
        var scope = new StaticBindingScope((RpcRegistrationRequirement)requirement, receipt);
        if (!scope.IsValid) throw Malformed("BindContract receipt is invalid or missing.");
        return new(contract, hash, scope);
    }

    internal static StaticBindingAck ReadAck(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != AckBytes)
            throw Malformed("BindAck must contain exactly 40 bytes.");
        var reader = new SequenceReader<byte>(payload);
        _ = reader.TryReadLittleEndian(out long status);
        _ = reader.TryReadLittleEndian(out long contract);
        _ = reader.TryReadLittleEndian(out long token);
        _ = reader.TryReadLittleEndian(out long registration);
        _ = reader.TryReadLittleEndian(out long generation);
        if (status < 0 || status > (long)StaticBindingFailure.ReceiptMismatch ||
            (status == 0 ? token == 0 || registration == 0 : token != 0 || registration != 0))
            throw Malformed("Invalid BindAck status/identity.");
        return new((StaticBindingFailure)status, contract, unchecked((ulong)token), registration, generation);
    }

    internal static ValueTask SendBindAsync(RpcSession session, ulong operation, long contract, RpcHash128 hash,
        CancellationToken cancellationToken, StaticBindingScope scope = default)
    {
        var writer = session.RentFrameWriter();
        try
        {
            using (writer.BeginPacketScope(Bind, ProtocolV2FrameFlags.None, operation))
            {
                if (!scope.IsValid) throw Malformed("Invalid Bind constraint.");
                var length = scope.Receipt.HasValue ? ReceiptRequestBytes : RequestBytes;
                var span = writer.GetSpan(length);
                BinaryPrimitives.WriteInt64LittleEndian(span, contract);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], hash.High);
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], hash.Low);
                BinaryPrimitives.WriteInt64LittleEndian(span[24..], (long)scope.Requirement);
                BinaryPrimitives.WriteInt64LittleEndian(span[32..], scope.Receipt.HasValue ? 1 : 0);
                if (scope.Receipt is { } receipt)
                {
                    receipt.ServerInstanceId.TryWriteBytes(span[40..56]); receipt.RegistryEpoch.TryWriteBytes(span[56..72]);
                    BinaryPrimitives.WriteInt64LittleEndian(span[72..], receipt.Generation);
                    BinaryPrimitives.WriteInt64LittleEndian(span[80..], receipt.ContractId);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[88..], receipt.RpcAssemblyHash.High);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[96..], receipt.RpcAssemblyHash.Low);
                    BinaryPrimitives.WriteInt64LittleEndian(span[104..], receipt.RegistrationId);
                }
                writer.Advance(length);
            }
        }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
        return session.SendPacketWithBackpressureAsync(writer, cancellationToken); // Ownership transfers on every outcome.
    }

    internal static ValueTask SendAckAsync(RpcSession session, ulong operation, StaticBindingAck ack,
        CancellationToken cancellationToken)
        => session.SendPacketAndFlushAsync(CreateAck(session, operation, ack), cancellationToken);

    // Refusal cannot wait for an exhausted operation slot. Synchronous publication uses
    // the existing bounded normal queue. Full/closed queues fail, without retry tasks.
    internal static void SendCapacityAck(RpcSession session, ulong operation, long contract)
        => session.SendPacket(CreateAck(session, operation,
            new StaticBindingAck(StaticBindingFailure.Capacity, contract, 0, 0, 0)));

    private static IRpcByteBufferWriter CreateAck(RpcSession session, ulong operation, StaticBindingAck ack)
    {
        var writer = session.RentFrameWriter();
        try
        {
            using (writer.BeginPacketScope(Ack, ProtocolV2FrameFlags.None, operation))
            {
                var span = writer.GetSpan(AckBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, (long)ack.Failure);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], ack.ContractId);
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], ack.Token);
                BinaryPrimitives.WriteInt64LittleEndian(span[24..], ack.RegistrationId);
                BinaryPrimitives.WriteInt64LittleEndian(span[32..], ack.Generation);
                writer.Advance(AckBytes);
            }
        }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
        return writer;
    }
}

internal enum StaticBindingFailure : ushort
{
    None, ContractMissing, HashMismatch, DynamicUnsupported, Capacity, Unavailable,
    UnknownBinding, UnknownMethod, UnsupportedShape, Retired, NoBoundRoute, ReceiptMismatch
}

internal readonly record struct StaticBindingRequest(long ContractId, RpcHash128 Hash, StaticBindingScope Scope);

internal readonly record struct StaticBindingAck(
    StaticBindingFailure Failure, long ContractId, ulong Token, long RegistrationId, long Generation);
