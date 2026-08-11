using System.IO.Pipelines;
using System.Net;

namespace SharpLink.UnitTests.Abstractions;

public class LegacyApiSurfaceTests
{
    [Test]
    public async Task EngineControlSurfaceShouldNotBeExportedAndApprovedSpisRemainImplementable()
    {
        var abstractions = typeof(IRpcGeneratedServerBridge).Assembly;
        var runtime = typeof(RpcSession).Assembly;

        foreach (var name in new[]
                 {
                     "SharpLink.Abstractions.IRpcSession",
                     "SharpLink.Abstractions.IStreamManager",
                     "SharpLink.Abstractions.IStreamDispatcher",
                     "SharpLink.Abstractions.IStreamConsumptionAwareDispatcher"
                 })
        {
            await Assert.That(abstractions.GetType(name, throwOnError: false)).IsNull();
        }

        foreach (var name in new[]
                 {
                     "SharpLink.Runtime.RpcSession",
                     "SharpLink.Runtime.StreamManager",
                     "SharpLink.Runtime.RpcSessionExtensions"
                 })
        {
            var engineType = runtime.GetType(name, throwOnError: false);
            await Assert.That(engineType is not null).IsTrue();
            await Assert.That(engineType!.IsPublic).IsFalse();
        }

        await Assert.That(typeof(IRpcGeneratedServerBridge).IsPublic).IsTrue();
        await Assert.That(typeof(ITransportConnection).IsAssignableFrom(typeof(ExternalTransport)))
            .IsTrue();
        await Assert.That(typeof(IRpcCodec<int>).IsAssignableFrom(typeof(ExternalCodec)))
            .IsTrue();
        await Assert.That(typeof(ISharpLinkServerInterceptor).IsAssignableFrom(typeof(ExternalInterceptor)))
            .IsTrue();
    }

    [Test]
    public async Task ObsoleteAndImplementationOnlyAbstractionsShouldNotBeExported()
    {
        var abstractions = typeof(IRpcGeneratedServerBridge).Assembly;
        var runtime = typeof(RpcSession).Assembly;
        var obsoleteAbstractions = new[]
        {
            "SharpLink.Abstractions.GeneratedProxyRegistry",
            "SharpLink.Abstractions.GeneratedStubRegistry",
            "SharpLink.Abstractions.ISerializer",
            "SharpLink.Abstractions.IServiceRegister",
            "SharpLink.Abstractions.RpcException"
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

    private sealed class ExternalTransport : ITransportConnection
    {
        public string Id => "external-transport";

        public PipeReader Input => PipeReader.Create(Stream.Null);

        public PipeWriter Output => PipeWriter.Create(Stream.Null);

        public EndPoint? LocalEndPoint => null;

        public EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExternalCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.ToArray());
    }

    private sealed class ExternalInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }
}
