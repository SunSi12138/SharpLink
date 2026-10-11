namespace SharpLink.Runtime;

internal static class ProtocolV2ContractManifestCodec
{
    private const int HeaderBytes = 1 + 16 + 16 + sizeof(long) + sizeof(int);
    private const int EntryBytes = sizeof(long) + sizeof(ulong) + sizeof(ulong) + 16;

    internal static void Write(IBufferWriter<byte> writer, ProtocolV2ContractManifest manifest, SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        var payloadBytes = checked(HeaderBytes + manifest.OrderedContracts.Count * EntryBytes);
        if (payloadBytes > limits.MaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(manifest), "Contract manifest exceeds the negotiated frame limit.");
        var span = writer.GetSpan(payloadBytes);
        span[0] = ProtocolV2ContractAdmissionCodec.Version;
        manifest.ServerInstanceId.TryWriteBytes(span[1..17]);
        manifest.RegistryEpoch.TryWriteBytes(span[17..33]);
        BinaryPrimitives.WriteInt64LittleEndian(span[33..], manifest.Generation);
        BinaryPrimitives.WriteInt32LittleEndian(span[41..], manifest.OrderedContracts.Count);
        var offset = HeaderBytes;
        for (var index = 0; index < manifest.OrderedContracts.Count; index++)
        {
            var pair = manifest.OrderedContracts[index];
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], pair.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], pair.Value.High);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 16)..], pair.Value.Low);
            manifest.RegistrationIds.TryGetValue(pair.Key, out var registrationId);
            registrationId.TryWriteBytes(span[(offset + 24)..(offset + EntryBytes)]);
            offset += EntryBytes;
        }
        writer.Advance(payloadBytes);
    }

    internal static ProtocolV2ContractManifest Read(ReadOnlySequence<byte> payload, SharpLinkProtocolOptions limits)
    {
        ValidatePayloadShape(payload, limits);
        var reader = new SequenceReader<byte>(payload);
        reader.Advance(1);
        var server = ProtocolV2ContractAdmissionCodec.ReadGuid(ref reader);
        var epoch = ProtocolV2ContractAdmissionCodec.ReadGuid(ref reader);
        reader.TryReadLittleEndian(out long generation);
        reader.TryReadLittleEndian(out int count);
        if ((server == Guid.Empty) != (epoch == Guid.Empty))
            throw ProtocolV2FrameParser.Violation("ContractManifest server and registry identities are inconsistent.");
        var entries = new KeyValuePair<long, RpcHash128>[count];
        var registrations = new KeyValuePair<long, Guid>[count];
        long previousContractId = 0;
        for (var index = 0; index < count; index++)
        {
            reader.TryReadLittleEndian(out long contractId);
            reader.TryReadLittleEndian(out long high);
            reader.TryReadLittleEndian(out long low);
            var registrationId = ProtocolV2ContractAdmissionCodec.ReadGuid(ref reader);
            if (contractId == 0 || index != 0 && contractId <= previousContractId)
                throw ProtocolV2FrameParser.Violation("ContractManifest contract IDs must be non-zero, unique, and strictly increasing.");
            var hash = new RpcHash128(unchecked((ulong)high), unchecked((ulong)low));
            if (hash.IsEmpty || server != Guid.Empty && registrationId == Guid.Empty)
                throw ProtocolV2FrameParser.Violation("ContractManifest contract or registration identity is empty.");
            entries[index] = new(contractId, hash);
            registrations[index] = new(contractId, registrationId);
            previousContractId = contractId;
        }
        return new ProtocolV2ContractManifest(server, epoch, generation, entries, registrations);
    }

    internal static void ValidatePayloadShape(ReadOnlySequence<byte> payload, SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length < HeaderBytes || payload.Length > limits.MaxFramePayloadBytes)
            throw ProtocolV2FrameParser.Violation("ContractManifest payload has an invalid bounded length.");
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var version) || version != ProtocolV2ContractAdmissionCodec.Version)
            throw ProtocolV2FrameParser.Violation("ContractManifest version is unsupported.");
        reader.Advance(32);
        if (!reader.TryReadLittleEndian(out long generation) || generation < 0 ||
            !reader.TryReadLittleEndian(out int count) || count < 0)
            throw ProtocolV2FrameParser.Violation("ContractManifest header is invalid.");
        var expected = HeaderBytes + (long)count * EntryBytes;
        if (payload.Length != expected)
            throw ProtocolV2FrameParser.Violation("ContractManifest entry count does not match the frame payload.");
    }
}
