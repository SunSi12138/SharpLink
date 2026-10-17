#if SHARPLINK_STATIC_BINDING_PROTOTYPE
namespace SharpLink.Runtime;

// Private, bounded experimental control protocol. These are not ordinary RPC methods.
internal static class StaticBindingWire
{
    internal const ProtocolV2FrameType Bind = (ProtocolV2FrameType)240;
    internal const ProtocolV2FrameType Ack = (ProtocolV2FrameType)241;
    internal const int RequestBytes = 24;
    internal const int AckBytes = 40;
    internal const int Capacity = 64;
    internal const int MaxMethods = 256;
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    internal static SharpLinkException Error(StaticBindingFailure failure)
        => new(failure switch
        {
            StaticBindingFailure.Capacity => SharpLinkErrorCode.ResourceExhausted,
            StaticBindingFailure.NoBoundRoute or StaticBindingFailure.Unavailable => SharpLinkErrorCode.Unavailable,
            StaticBindingFailure.UnknownMethod => SharpLinkErrorCode.Unimplemented,
            _ => SharpLinkErrorCode.FailedPrecondition
        }, (ushort)(60000 + (ushort)failure), $"Static binding prototype: {failure}.");

    internal static SharpLinkProtocolViolationException Malformed(string message)
        => new(ProtocolViolationReason.MalformedFrame, message);

    internal static (long ContractId, RpcHash128 Hash) ReadRequest(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != RequestBytes)
            throw Malformed("BindContract must contain exactly 24 bytes.");
        var reader = new SequenceReader<byte>(payload);
        _ = reader.TryReadLittleEndian(out long contract);
        _ = reader.TryReadLittleEndian(out long high);
        _ = reader.TryReadLittleEndian(out long low);
        return (contract, new RpcHash128(unchecked((ulong)high), unchecked((ulong)low)));
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
        if (status < 0 || status > (long)StaticBindingFailure.NoBoundRoute ||
            (status == 0 ? token == 0 || registration == 0 : token != 0 || registration != 0))
            throw Malformed("Invalid BindAck status/identity.");
        return new((StaticBindingFailure)status, contract, unchecked((ulong)token), registration, generation);
    }

    internal static void SendBind(RpcSession session, ulong operation, long contract, RpcHash128 hash)
    {
        var writer = session.RentFrameWriter();
        try
        {
            using (writer.BeginPacketScope(Bind, ProtocolV2FrameFlags.None, operation))
            {
                var span = writer.GetSpan(RequestBytes);
                BinaryPrimitives.WriteInt64LittleEndian(span, contract);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], hash.High);
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], hash.Low);
                writer.Advance(RequestBytes);
            }
        }
        catch { session.RuntimeContext.Buffers.Return(writer); throw; }
        session.SendPacket(writer); // Ownership transfers even on enqueue failure.
    }

    internal static ValueTask SendAckAsync(RpcSession session, ulong operation, StaticBindingAck ack,
        CancellationToken cancellationToken)
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
        return session.SendPacketAndFlushAsync(writer, cancellationToken);
    }
}

internal enum StaticBindingFailure : ushort
{
    None, ContractMissing, HashMismatch, DynamicUnsupported, Capacity, Unavailable,
    UnknownBinding, UnknownMethod, UnsupportedShape, NoBoundRoute
}

internal readonly record struct StaticBindingAck(
    StaticBindingFailure Failure, long ContractId, ulong Token, long RegistrationId, long Generation);
#endif
