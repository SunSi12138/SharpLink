using System.Collections.Generic;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public class SharpLinkRuntimeContextTests
{
    [Test]
    public void ProcessDefaultShouldNotSnapshotGeneratedAssemblyCatalog()
    {
        var manifest = new CatalogManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);

        var instanceContext = new SharpLinkRuntimeContextBuilder().Build();
        var processDefault = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);

        Ensure(instanceContext.Codecs.GetCodec<CatalogValue>() is CatalogCodec,
            "instance context snapshots generated manifest codecs");
        try
        {
            _ = processDefault.Codecs.GetCodec<CatalogValue>();
            throw new Exception("process default must not capture a catalog codec");
        }
        catch (NotSupportedException)
        {
        }
        GC.KeepAlive(manifest);
    }

    [Test]
    public void DefaultOptionsShouldMatchBalancedProfile()
    {
        var context = new SharpLinkRuntimeContextBuilder().Build();
        var options = context.Options;

        Ensure(options.PerformanceProfile == SharpLinkPerformanceProfile.Balanced, "balanced profile");
        Ensure(options.Protocol.MaxFramePayloadBytes == 4 * 1024 * 1024, "frame limit");
        Ensure(options.Protocol.MaxMetadataBytes == 16 * 1024, "metadata limit");
        Ensure(options.Protocol.MaxErrorMessageBytes == 64 * 1024, "error limit");
        Ensure(options.Protocol.HandshakeTimeout == TimeSpan.FromSeconds(10), "handshake timeout");
        Ensure(options.FlowControl.MaxSendQueueBytes == 8 * 1024 * 1024, "balanced queue");
        Ensure(options.FlowControl.StreamReceiveWindowBytes == 1024 * 1024, "stream window");
        Ensure(options.FlowControl.ConnectionReceiveWindowBytes == 16 * 1024 * 1024, "connection window");
        Ensure(options.FlowControl.MaxConcurrentCallsPerConnection == 1024, "call limit");
    }

    [Test]
    public void PerformanceProfilesShouldApplyQueueDefaults()
    {
        var lowLatency = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)
            .Build();
        var throughput = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build();

        Ensure(lowLatency.Options.FlowControl.MaxSendQueueBytes == 1024 * 1024, "low-latency queue");
        Ensure(throughput.Options.FlowControl.MaxSendQueueBytes == 32 * 1024 * 1024, "throughput queue");
    }

    [Test]
    public void BuiltInCodecShouldBeImmutable()
    {
        try
        {
            new SharpLinkRuntimeContextBuilder().AddCodec(new ReplacementInt32Codec());
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("immutable", StringComparison.Ordinal), "immutable codec error");
            return;
        }

        throw new Exception("Expected built-in codec replacement to be rejected.");
    }

    [Test]
    public void BuildShouldFreezeOptionsPoolAndStateStoreSnapshots()
    {
        var builder = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Protocol.MaxFramePayloadBytes = 2048)
            .ConfigureBufferPool(options => options.InitialCapacity = 1234)
            .ConfigureStateStores(options => options.StripeCount = 8);
        var first = builder.Build();

        builder.Configure(options => options.Protocol.MaxFramePayloadBytes = 4096)
            .ConfigureBufferPool(options => options.InitialCapacity = 2345)
            .ConfigureStateStores(options => options.StripeCount = 16);
        var second = builder.Build();

        Ensure(first.Options.Protocol.MaxFramePayloadBytes == 2048, "first protocol snapshot");
        Ensure(second.Options.Protocol.MaxFramePayloadBytes == 4096, "second protocol snapshot");
        Ensure(first.Buffers.InitialCapacity == 1234, "first pool snapshot");
        Ensure(second.Buffers.InitialCapacity == 2345, "second pool snapshot");
        Ensure(first.Concurrency.StripeCount == 8, "first stripe snapshot");
        Ensure(second.Concurrency.StripeCount == 16, "second stripe snapshot");

        var leakedCopy = first.Options;
        leakedCopy.Protocol.MaxFramePayloadBytes = 8192;
        Ensure(first.Options.Protocol.MaxFramePayloadBytes == 2048, "returned options must be isolated copies");
    }

    [Test]
    public async Task BuildingOneHundredContextsInParallelShouldNotCrossContaminate()
    {
        var tasks = new Task<SharpLinkRuntimeContext>[100];
        for (var index = 0; index < tasks.Length; index++)
        {
            var captured = index;
            tasks[index] = Task.Run(() =>
            {
                var codec = new TaggedCodec(captured);
                return new SharpLinkRuntimeContextBuilder()
                    .Configure(options => options.Protocol.MaxMetadataBytes = 1024 + captured)
                    .ConfigureBufferPool(options => options.InitialCapacity = 1024 + captured)
                    .ConfigureStateStores(options => options.StripeCount = captured % 2 == 0 ? 8 : 16)
                    .AddCodec(codec)
                    .Build();
            });
        }

        var contexts = await Task.WhenAll(tasks);
        for (var index = 0; index < contexts.Length; index++)
        {
            var context = contexts[index];
            Ensure(context.Options.Protocol.MaxMetadataBytes == 1024 + index, $"metadata snapshot {index}");
            Ensure(context.Buffers.InitialCapacity == 1024 + index, $"pool snapshot {index}");
            Ensure(context.Codecs.GetCodec<TaggedValue>() is TaggedCodec { Tag: var tag } && tag == index,
                $"codec snapshot {index}");
            Ensure(context.Concurrency.StripeCount == (index % 2 == 0 ? 8 : 16), $"stripe snapshot {index}");
        }
    }

    private sealed class TaggedValue;

    private sealed class TaggedCodec(int tag) : IRpcCodec<TaggedValue>
    {
        public int Tag { get; } = tag;

        public void Serialize(in TaggedValue value, IBufferWriter<byte> buffer)
        {
        }

        public TaggedValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class ReplacementInt32Codec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer) => 0;
    }

    private sealed class CatalogValue;

    private sealed class CatalogCodec : IRpcCodec<CatalogValue>
    {
        public void Serialize(in CatalogValue value, IBufferWriter<byte> buffer)
        {
        }

        public CatalogValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class CatalogCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(CatalogValue);
        public string SchemaId => "catalog-test-v1";
        public IRpcCodec Create(IRpcCodecProvider provider) => new CatalogCodec();
    }

    private sealed class CatalogManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(CatalogManifest).Assembly;
        public string CompileTimeDescriptor => "catalog-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new CatalogCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
