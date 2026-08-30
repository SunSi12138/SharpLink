namespace SharpLink.UnitTests;

internal interface ISharpLinkGeneratedAssemblyManifest : SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest
{
    RpcHash128 SharpLink.Abstractions.ISharpLinkGeneratedAssemblyManifest.RpcAssemblyHash
        => new(0x746573742d6d616eUL, 0x69666573742d7631UL);
}

internal interface IRpcGeneratedCodecFactory : SharpLink.Abstractions.IRpcGeneratedCodecFactory
{
    RpcHash128 SharpLink.Abstractions.IRpcGeneratedCodecFactory.CodecHash
        => new(0x746573742d636f64UL, 0x65632d6861736831UL);
}
