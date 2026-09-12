namespace SharpLink.UnitTests;

internal static class TestGeneratedIdentity
{
    internal static readonly RpcHash128 ManifestHash =
        new(0x746573742d6d616eUL, 0x69666573742d7631UL);

    internal static readonly RpcHash128 CodecHash =
        new(0x746573742d636f64UL, 0x65632d6861736831UL);

    internal static readonly RpcHash128 AlternateCodecHash =
        new(0x746573742d636f64UL, 0x65632d6861736832UL);
}

internal interface ITestGeneratedManifest : ISharpLinkGeneratedAssemblyManifest
{
    RpcHash128 ISharpLinkGeneratedAssemblyManifest.RpcAssemblyHash
        => TestGeneratedIdentity.ManifestHash;
}

internal interface ITestGeneratedCodecFactory : IRpcGeneratedCodecFactory
{
    RpcHash128 IRpcGeneratedCodecFactory.CodecHash
        => TestGeneratedIdentity.CodecHash;
}
