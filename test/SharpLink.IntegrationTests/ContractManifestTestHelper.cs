namespace SharpLink.IntegrationTests;

internal static class ContractManifestTestHelper
{
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
                [KeyValuePair.Create(contract.ContractId, localManifest.RpcAssemblyHash)]),
            new SharpLinkProtocolOptions());
        ProtocolV2FrameWriter.EndFrame(writer, manifestToken);
    }
}
