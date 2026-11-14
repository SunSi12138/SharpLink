namespace SharpLink.IntegrationTests;

internal static class ContractManifestTestHelper
{
    private const ulong TestBinding = (1UL << 32) | 1;
    internal static void EndHandshakeAndWriteManifest(
        PooledByteBufferWriter writer,
        ProtocolV2FrameToken handshakeToken,
        Type contractType)
    {
        ProtocolV2FrameWriter.EndFrame(writer, handshakeToken);
        var localManifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
            candidate.Contracts.Any(contract => contract.ContractType == contractType));
        var contract = localManifest.Contracts.Single(candidate => candidate.ContractType == contractType);
        var manifestToken = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None,
            0);
        ProtocolV2ContractManifestCodec.Write(
            writer,
            new ProtocolV2ContractManifest(
                0,
                [KeyValuePair.Create(contract.ContractId, localManifest.RpcAssemblyHash)])
                { ServerInstanceId = Guid.NewGuid(), RegistryEpoch = Guid.NewGuid() },
            new SharpLinkProtocolOptions());
        ProtocolV2FrameWriter.EndFrame(writer, manifestToken);
    }

    internal static void WriteBindAck(PooledByteBufferWriter writer, ulong operation,
        ReadOnlySequence<byte> payload, Type contractType)
    {
        var request = StaticBindingWire.ReadRequest(payload);
        var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
            candidate.Contracts.Any(contract => contract.ContractType == contractType));
        var contract = manifest.Contracts.Single(candidate => candidate.ContractType == contractType);
        if (request.ContractId != contract.ContractId || request.Hash != manifest.RpcAssemblyHash ||
            request.Scope.Requirement != RpcRegistrationRequirement.Compatible || request.Scope.Receipt.HasValue)
            throw new InvalidOperationException("The simulated peer received an unexpected Bind identity/scope.");
        var frame = ProtocolV2FrameWriter.BeginFrame(writer, ProtocolV2FrameType.BindAck,
            ProtocolV2FrameFlags.None, operation);
        var bytes = writer.GetSpan(StaticBindingWire.AckBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], contract.ContractId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], TestBinding);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], 1);
        writer.Advance(StaticBindingWire.AckBytes);
        ProtocolV2FrameWriter.EndFrame(writer, frame);
    }

    internal static void EnsureBoundPrefix(ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long binding) || unchecked((ulong)binding) != TestBinding)
            throw new InvalidOperationException("Business traffic must use the confirmed Session-local binding.");
    }
}
