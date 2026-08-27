using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;

namespace SharpLink.UnitTests.Abstractions;

public class LegacyApiSurfaceTests
{
    private static readonly string[] RuntimeRawDispatcherTypeNames =
    [
        "SharpLink.Runtime.IStreamDispatcher",
        "SharpLink.Runtime.IStreamConsumptionAwareDispatcher",
        "SharpLink.Runtime.IStreamDispatchLease",
        "SharpLink.Runtime.IStreamDispatchState",
        "SharpLink.Runtime.InboundStreamChildDispatchState",
        "SharpLink.Runtime.PooledAsyncStreamDispatcher`1",
        "SharpLink.Runtime.PreAdmissionStreamDispatcher",
        "SharpLink.Runtime.DiscardingStreamDispatcher"
    ];

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
    public async Task RawStreamDispatcherTypesShouldNotBeExported()
    {
        var abstractions = typeof(IRpcGeneratedServerBridge).Assembly;
        var runtime = typeof(RpcSession).Assembly;
        var rawDispatcherTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var name in RuntimeRawDispatcherTypeNames)
        {
            var rawDispatcherType = runtime.GetType(name, throwOnError: false);
            await Assert.That(rawDispatcherType).IsNotNull();
            var requiredRawDispatcherType = rawDispatcherType!;
            await Assert.That(requiredRawDispatcherType.IsPublic).IsFalse();
            await Assert.That(requiredRawDispatcherType.IsNestedPublic).IsFalse();
            await Assert.That(requiredRawDispatcherType.IsVisible).IsFalse();
            rawDispatcherTypes.Add(name, requiredRawDispatcherType);
        }

        var streamDispatcher = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatcher"];
        var dispatchLease = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatchLease"];
        var dispatchState = rawDispatcherTypes["SharpLink.Runtime.IStreamDispatchState"];
        var discoveredRawDispatcherTypeNames = runtime.GetTypes()
            .Where(type =>
                !type.IsNested &&
                (streamDispatcher.IsAssignableFrom(type) ||
                 dispatchLease.IsAssignableFrom(type) ||
                 dispatchState.IsAssignableFrom(type)))
            .Select(static type => type.FullName!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(discoveredRawDispatcherTypeNames)
            .IsEquivalentTo(RuntimeRawDispatcherTypeNames);

        var explicitDenylist = RuntimeRawDispatcherTypeNames.ToHashSet(StringComparer.Ordinal);
        var explicitlyDeniedExports = runtime.GetExportedTypes()
            .Select(static type => type.FullName)
            .Where(name => name is not null && explicitDenylist.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        await Assert.That(explicitlyDeniedExports.Length).IsEqualTo(0);

        var exportedRawDispatchers = new[]
            {
                abstractions,
                runtime
            }
            .SelectMany(static assembly => assembly.GetExportedTypes())
            .Where(static type =>
                type.Name.Contains("Dispatcher", StringComparison.Ordinal) ||
                type.Name is "IStreamDispatchLease" or "IStreamDispatchState")
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(exportedRawDispatchers.Length).IsEqualTo(0);
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
