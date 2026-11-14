namespace SharpLink.IntegrationTests;

// Raw-wire controls still bootstrap the real server binding. Their intentionally malformed
// business payload must not be rejected for an unrelated retired routing prefix first.
internal static class RawContractBindingTestHelper
{
    internal static async Task<(ulong Token, SharpLinkGeneratedContractDescriptor Contract)> BindAsync(
        Stream stream, Type contractType, SharpLinkProtocolOptions limits, CancellationToken cancellationToken)
    {
        var handshake = await ReadFrameAsync(stream, limits, cancellationToken);
        if (handshake.Header.Type != ProtocolV2FrameType.HandshakeResponse ||
            handshake.Header.RequestId != 0 || handshake.Header.Flags != ProtocolV2FrameFlags.None)
            throw new InvalidOperationException("Raw control requires a successful current-wire handshake.");

        var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
            candidate.Contracts.Any(contract => contract.ContractType == contractType));
        var contract = manifest.Contracts.Single(candidate => candidate.ContractType == contractType);
        using var request = new PooledByteBufferWriter();
        var frame = ProtocolV2FrameWriter.BeginFrame(request, ProtocolV2FrameType.BindContract,
            ProtocolV2FrameFlags.None, 1);
        var payload = new byte[StaticBindingWire.RequestBytes];
        BinaryPrimitives.WriteInt64LittleEndian(payload, contract.ContractId);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), manifest.RpcAssemblyHash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), manifest.RpcAssemblyHash.Low);
        request.Write(payload);
        ProtocolV2FrameWriter.EndFrame(request, frame);
        await stream.WriteAsync(request.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var reply = await ReadFrameAsync(stream, limits, cancellationToken);
        if (reply.Header.Type != ProtocolV2FrameType.BindAck || reply.Header.RequestId != 1 ||
            reply.Header.Flags != ProtocolV2FrameFlags.None)
            throw new InvalidOperationException("Raw control did not receive its exact BindAck.");
        var ack = StaticBindingWire.ReadAck(new ReadOnlySequence<byte>(reply.Payload));
        if (ack.Failure != StaticBindingFailure.None || ack.ContractId != contract.ContractId)
            throw new InvalidOperationException($"Raw control binding failed: {ack.Failure}.");
        return (ack.Token, contract);
    }

    private static async Task<(ProtocolV2FrameHeader Header, byte[] Payload)> ReadFrameAsync(
        Stream stream, SharpLinkProtocolOptions limits, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[ProtocolV2Constants.HeaderBytes];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(1));
        if (length < 0 || length > limits.MaxFramePayloadBytes)
            throw new InvalidOperationException("Raw control received an unbounded frame length.");
        var bytes = new byte[ProtocolV2Constants.HeaderBytes + length];
        headerBytes.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(ProtocolV2Constants.HeaderBytes), cancellationToken);
        var sequence = new ReadOnlySequence<byte>(bytes);
        if (!ProtocolV2FrameParser.TryReadFrame(ref sequence, limits, out var header, out var payload) ||
            !sequence.IsEmpty)
            throw new InvalidOperationException("Raw control received an incomplete frame.");
        return (header, payload.ToArray());
    }
}
