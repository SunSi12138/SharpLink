namespace SharpLink.Runtime;

internal static class ProtocolV2ContractManifestCodec
{
#if SHARPLINK_775_STATIC_SLICE
    private const int HeaderBytes = sizeof(long) + sizeof(int) + 32;
#else
    private const int HeaderBytes = sizeof(long) + sizeof(int);
#endif
    private const int EntryBytes = sizeof(long) + sizeof(ulong) + sizeof(ulong);

    internal static void Write(
        IBufferWriter<byte> writer,
        ProtocolV2ContractManifest manifest,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        var payloadBytes = checked(HeaderBytes + manifest.OrderedContracts.Count * EntryBytes);
        if (payloadBytes > limits.MaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(manifest), "Contract manifest exceeds the negotiated frame limit.");

        var span = writer.GetSpan(payloadBytes);
        BinaryPrimitives.WriteInt64LittleEndian(span, manifest.Generation);
        BinaryPrimitives.WriteInt32LittleEndian(span[sizeof(long)..], manifest.OrderedContracts.Count);
#if SHARPLINK_775_STATIC_SLICE
        if (manifest.ServerInstanceId == Guid.Empty || manifest.RegistryEpoch == Guid.Empty)
            throw new ArgumentException("A binding Session requires a non-empty server/epoch identity.", nameof(manifest));
        manifest.ServerInstanceId.TryWriteBytes(span[12..28]);
        manifest.RegistryEpoch.TryWriteBytes(span[28..44]);
#endif
        var offset = HeaderBytes;
        for (var index = 0; index < manifest.OrderedContracts.Count; index++)
        {
            var pair = manifest.OrderedContracts[index];
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], pair.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + sizeof(long))..], pair.Value.High);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + sizeof(long) + sizeof(ulong))..], pair.Value.Low);
            offset += EntryBytes;
        }
        writer.Advance(payloadBytes);
    }

    internal static ProtocolV2ContractManifest Read(
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        ValidatePayloadShape(payload, limits);
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long generation) ||
            !reader.TryReadLittleEndian(out int count))
        {
            throw ProtocolV2FrameParser.Violation("ContractManifest payload is truncated.");
        }

#if SHARPLINK_775_STATIC_SLICE
        Span<byte> identity = stackalloc byte[32];
        if (!reader.TryCopyTo(identity)) throw ProtocolV2FrameParser.Violation("ContractManifest identity is truncated.");
        reader.Advance(32);
        var server = new Guid(identity[..16]); var epoch = new Guid(identity[16..]);
        if (server == Guid.Empty || epoch == Guid.Empty)
            throw ProtocolV2FrameParser.Violation("ContractManifest identity cannot be empty.");
#endif
        var entries = new KeyValuePair<long, RpcHash128>[count];
        long previousContractId = 0;
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadLittleEndian(out long contractId) ||
                !reader.TryReadLittleEndian(out long highBits) ||
                !reader.TryReadLittleEndian(out long lowBits))
            {
                throw ProtocolV2FrameParser.Violation("ContractManifest entry is truncated.");
            }
            if (contractId == 0 || index != 0 && contractId <= previousContractId)
                throw ProtocolV2FrameParser.Violation("ContractManifest contract IDs must be non-zero, unique, and strictly increasing.");
            var hash = new RpcHash128(unchecked((ulong)highBits), unchecked((ulong)lowBits));
            if (hash.IsEmpty)
                throw ProtocolV2FrameParser.Violation("ContractManifest RpcAssemblyHash cannot be empty.");
            entries[index] = new KeyValuePair<long, RpcHash128>(contractId, hash);
            previousContractId = contractId;
        }
#if SHARPLINK_775_STATIC_SLICE
        return new ProtocolV2ContractManifest(generation, entries) { ServerInstanceId = server, RegistryEpoch = epoch };
#else
        return new ProtocolV2ContractManifest(generation, entries);
#endif
    }

    internal static void ValidatePayloadShape(
        ReadOnlySequence<byte> payload,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length < HeaderBytes || payload.Length > limits.MaxFramePayloadBytes)
            throw ProtocolV2FrameParser.Violation("ContractManifest payload has an invalid bounded length.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long generation) || generation < 0 ||
            !reader.TryReadLittleEndian(out int count) || count < 0)
        {
            throw ProtocolV2FrameParser.Violation("ContractManifest header is invalid.");
        }
        long expected;
        try
        {
            expected = checked(HeaderBytes + (long)count * EntryBytes);
        }
        catch (OverflowException)
        {
            throw ProtocolV2FrameParser.Violation("ContractManifest entry count is invalid.");
        }
        if (payload.Length != expected)
            throw ProtocolV2FrameParser.Violation("ContractManifest entry count does not match the frame payload.");
    }
}
