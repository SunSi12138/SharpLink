namespace SharpLink.IntegrationTests;


internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver);
    }
}