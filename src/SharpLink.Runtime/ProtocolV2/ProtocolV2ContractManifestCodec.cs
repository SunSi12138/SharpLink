namespace SharpLink.Runtime;

internal static class ProtocolV2ContractManifestCodec
{
    private const int HeaderBytes = sizeof(long) + sizeof(int);
    private const int EntryBytes = sizeof(long) + sizeof(ulong) + sizeof(ulong);
    private const int RouteExtensionMagic = 0x31544552;
    private const int RouteHeaderBytes = sizeof(int) + 16;

    internal static void Write(
        IBufferWriter<byte> writer,
        ProtocolV2ContractManifest manifest,
        SharpLinkProtocolOptions limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(limits);
        var payloadBytes = checked(HeaderBytes + manifest.OrderedContracts.Count * EntryBytes +
            (manifest.RouteScope == Guid.Empty ? 0 : RouteHeaderBytes + manifest.OrderedContracts.Count * sizeof(long)));
        if (payloadBytes > limits.MaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(manifest), "Contract manifest exceeds the negotiated frame limit.");

        var span = writer.GetSpan(payloadBytes);
        BinaryPrimitives.WriteInt64LittleEndian(span, manifest.Generation);
        BinaryPrimitives.WriteInt32LittleEndian(span[sizeof(long)..], manifest.OrderedContracts.Count);
        var offset = HeaderBytes;
        for (var index = 0; index < manifest.OrderedContracts.Count; index++)
        {
            var pair = manifest.OrderedContracts[index];
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], pair.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + sizeof(long))..], pair.Value.High);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + sizeof(long) + sizeof(ulong))..], pair.Value.Low);
            offset += EntryBytes;
        }
        if (manifest.RouteScope != Guid.Empty)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], RouteExtensionMagic);
            manifest.RouteScope.TryWriteBytes(span[(offset + sizeof(int))..]);
            offset += RouteHeaderBytes;
            foreach (var entry in manifest.OrderedContracts)
            {
                BinaryPrimitives.WriteInt64LittleEndian(span[offset..], manifest.Routes[entry.Key]);
                offset += sizeof(long);
            }
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
        if (reader.Remaining == 0)
            return new ProtocolV2ContractManifest(generation, entries);
        if (!reader.TryReadLittleEndian(out int marker) || marker != RouteExtensionMagic)
            throw ProtocolV2FrameParser.Violation("Unknown ContractManifest extension.");
        Span<byte> scopeBytes = stackalloc byte[16];
        if (!reader.TryCopyTo(scopeBytes))
            throw ProtocolV2FrameParser.Violation("Prepared route scope is truncated.");
        reader.Advance(16);
        var scope = new Guid(scopeBytes);
        if (scope == Guid.Empty)
            throw ProtocolV2FrameParser.Violation("Prepared route scope is empty.");
        var routes = new KeyValuePair<long, long>[count];
        var seenRoutes = new HashSet<long>();
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadLittleEndian(out long route) || route <= 0 || !seenRoutes.Add(route))
                throw ProtocolV2FrameParser.Violation("Prepared route IDs must be positive and unique.");
            routes[index] = new(entries[index].Key, route);
        }
        return new ProtocolV2ContractManifest(generation, entries, scope, routes);
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
        var extended = checked(expected + RouteHeaderBytes + (long)count * sizeof(long));
        if (payload.Length != expected && payload.Length != extended)
            throw ProtocolV2FrameParser.Violation("ContractManifest entry count does not match the frame payload.");
    }
}
