namespace SharpLink.Runtime;

/// <summary>Scalar request identity available before any business payload is decoded.</summary>
internal readonly record struct ProtocolV2RequestIdentity(
    long ContractId,
    long MethodId,
    RpcHash128 RpcAssemblyHash,
    RpcMethodKind Kind,
    ushort ClientStreamCount,
    ContractRegistrationReceipt? Receipt = null,
    RpcRegistrationRequirement Requirement = RpcRegistrationRequirement.Compatible);

/// <summary>Owns the bounded, uncompressed request identity prefix of wire generation five.</summary>
internal static class ProtocolV2RequestIdentityCodec
{
    internal const int ReceiptBytes = 56;
    private const int ModeOffset = ProtocolV2Constants.RequestPrefixBytes - 1;

    internal static int GetPrefixLength(in ReadOnlySequence<byte> payload)
    {
        if (payload.Length < ProtocolV2Constants.RequestPrefixBytes)
            throw ProtocolV2FrameParser.Violation("Request identity prefix is truncated.");
        var mode = payload.FirstSpan.Length > ModeOffset
            ? payload.FirstSpan[ModeOffset]
            : ReadMode(payload);
        if (mode > 3)
            throw ProtocolV2FrameParser.Violation("Request registration requirement is invalid.");
        var length = ProtocolV2Constants.RequestPrefixBytes + (mode == 0 ? 0 : ReceiptBytes);
        if (payload.Length < length)
            throw ProtocolV2FrameParser.Violation("Request registration receipt is truncated.");
        return length;
    }

    private static byte ReadMode(in ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        reader.Advance(ModeOffset);
        reader.TryRead(out var mode);
        return mode;
    }

    internal static void Write(IBufferWriter<byte> writer, in ProtocolV2RequestIdentity identity)
    {
        Validate(identity);
        var length = ProtocolV2Constants.RequestPrefixBytes + (identity.Receipt.HasValue ? ReceiptBytes : 0);
        var span = writer.GetSpan(length);
        BinaryPrimitives.WriteInt64LittleEndian(span, identity.ContractId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], identity.MethodId);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], identity.RpcAssemblyHash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], identity.RpcAssemblyHash.Low);
        span[32] = (byte)identity.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(span[33..], identity.ClientStreamCount);
        span[ModeOffset] = identity.Receipt.HasValue ? checked((byte)((byte)identity.Requirement + 1)) : (byte)0;
        if (identity.Receipt is { } receipt)
        {
            var extension = span[ProtocolV2Constants.RequestPrefixBytes..];
            receipt.ServerInstanceId.TryWriteBytes(extension);
            receipt.RegistryEpoch.TryWriteBytes(extension[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(extension[32..], receipt.Generation);
            receipt.RegistrationId.TryWriteBytes(extension[40..]);
        }
        writer.Advance(length);
    }

    internal static ProtocolV2RequestIdentity Read(ref SequenceReader<byte> reader)
    {
        if (!reader.TryReadLittleEndian(out long contractId) ||
            !reader.TryReadLittleEndian(out long methodId) ||
            !reader.TryReadLittleEndian(out long high) ||
            !reader.TryReadLittleEndian(out long low) ||
            !reader.TryRead(out var kind) ||
            !reader.TryReadLittleEndian(out short streamCount) ||
            !reader.TryRead(out var mode))
            throw ProtocolV2FrameParser.Violation("Request identity prefix is truncated.");
        if (mode > 3)
            throw ProtocolV2FrameParser.Violation("Request registration requirement is invalid.");
        var hash = new RpcHash128(unchecked((ulong)high), unchecked((ulong)low));
        ContractRegistrationReceipt? receipt = null;
        var requirement = RpcRegistrationRequirement.Compatible;
        if (mode != 0)
        {
            var server = ReadGuid(ref reader);
            var epoch = ReadGuid(ref reader);
            if (!reader.TryReadLittleEndian(out long generation))
                throw ProtocolV2FrameParser.Violation("Request receipt generation is truncated.");
            var registration = ReadGuid(ref reader);
            receipt = new ContractRegistrationReceipt(server, epoch, unchecked((ulong)generation),
                contractId, hash, registration);
            requirement = (RpcRegistrationRequirement)(mode - 1);
        }
        var identity = new ProtocolV2RequestIdentity(contractId, methodId, hash,
            (RpcMethodKind)kind, unchecked((ushort)streamCount), receipt, requirement);
        Validate(identity);
        return identity;
    }

    private static void Validate(in ProtocolV2RequestIdentity identity)
    {
        if (identity.ContractId == 0 || identity.RpcAssemblyHash.IsEmpty)
            throw ProtocolV2FrameParser.Violation("Request contract identity is empty.");
        if (identity.Kind > RpcMethodKind.DuplexStreaming ||
            identity.Requirement > RpcRegistrationRequirement.ExactRegistration)
            throw ProtocolV2FrameParser.Violation("Request invocation shape or registration requirement is invalid.");
        var streaming = identity.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        if (streaming && identity.ClientStreamCount == 0 ||
            identity.Kind is RpcMethodKind.Unary or RpcMethodKind.ServerStreaming && identity.ClientStreamCount != 0)
            throw ProtocolV2FrameParser.Violation("Request invocation shape and client stream count disagree.");
        if (identity.Receipt is { } receipt)
        {
            if (receipt.ServerInstanceId == Guid.Empty || receipt.RegistryEpoch == Guid.Empty ||
                receipt.RegistrationId == Guid.Empty ||
                receipt.ContractId != identity.ContractId || receipt.RpcAssemblyHash != identity.RpcAssemblyHash)
                throw ProtocolV2FrameParser.Violation("Request registration receipt identity is invalid.");
        }
        else if (identity.Requirement != RpcRegistrationRequirement.Compatible)
            throw ProtocolV2FrameParser.Violation("A version requirement needs a registration receipt.");
    }

    internal static Guid ReadGuid(ref SequenceReader<byte> reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!reader.TryCopyTo(bytes))
            throw ProtocolV2FrameParser.Violation("Registry identity is truncated.");
        reader.Advance(bytes.Length);
        return new Guid(bytes);
    }
}
