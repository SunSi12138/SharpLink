namespace SharpLink.UnitTests.Abstractions;

public class LegacyApiSurfaceTests
{
    [Test]
    public async Task ObsoleteAndImplementationOnlyAbstractionsShouldNotBeExported()
    {
        var abstractions = typeof(IRpcSession).Assembly;
        var runtime = typeof(RpcSession).Assembly;
        var obsoleteAbstractions = new[]
        {
            "SharpLink.Abstractions.GeneratedProxyRegistry",
            "SharpLink.Abstractions.GeneratedStubRegistry",
            "SharpLink.Abstractions.ISerializer",
            "SharpLink.Abstractions.IServiceRegister"
        };
        foreach (var name in obsoleteAbstractions)
            await Assert.That(abstractions.GetType(name, throwOnError: false)).IsNull();

        await Assert.That(runtime.GetType("SharpLink.Runtime.StripedLongSet", throwOnError: false))
            .IsNull();
        foreach (var name in new[]
                 {
                     "SharpLink.Runtime.StripedLongMap`1",
                     "SharpLink.Runtime.RpcBufferWriterExtensions",
                     "SharpLink.Runtime.PacketToken",
                     "SharpLink.Runtime.PacketScope",
                     "SharpLink.Runtime.ProtocolV2FrameWriter",
                     "SharpLink.Runtime.ProtocolV2FrameToken"
                 })
        {
            await Assert.That(runtime.GetType(name, throwOnError: false)?.IsPublic ?? false)
                .IsFalse();
        }
    }
}
