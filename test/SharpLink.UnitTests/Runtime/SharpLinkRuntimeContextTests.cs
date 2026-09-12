using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public partial class SharpLinkRuntimeContextTests
{
    private static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);
    private static readonly RpcHash128 TestAssemblyHash = new(0x72756e74696d652dUL, 0x746573742d763031UL);

    [Test]
    // This is the intentional default-global adapter test; the other RuntimeContext tests use fixed sources.
    [NotInParallel("generated-catalog")]
    public void ExplicitCatalogFreeContextShouldNotSnapshotGeneratedAssemblyCatalog()
    {
        var catalogCountBefore = RollbackTestIsolation.AssemblyManifestCount;
        var manifest = new CatalogManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            using var instanceContext = new SharpLinkRuntimeContextBuilder().Build();
            using var catalogFreeContext = CreateRuntimeBuilder()
                .Build(includeGeneratedAssemblyCatalog: false);

            Ensure(instanceContext.Codecs.GetCodec<CatalogValue>() is CatalogCodec,
                "instance context snapshots generated manifest codecs");
            try
            {
                _ = catalogFreeContext.Codecs.GetCodec<CatalogValue>();
                throw new Exception("an explicit catalog-free context must not capture a catalog codec");
            }
            catch (NotSupportedException)
            {
            }
            GC.KeepAlive(manifest);
        }
        finally
        {
            Ensure(RollbackTestIsolation.RemoveManifestFromCatalog(manifest),
                "the default-global adapter test must remove only its manifest identity");
            Ensure(RollbackTestIsolation.AssemblyManifestCount <= catalogCountBefore,
                "the default-global adapter test must not grow the live catalog");
        }
    }

    [Test]
    public void DefaultOptionsShouldMatchBalancedProfile()
    {
        var context = CreateRuntimeBuilder().Build();
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
        Ensure(options.FlowControl.MaxConcurrentCallsPerServer ==
               SharpLinkFlowControlOptions.DefaultMaxConcurrentCallsPerServer,
            "server-wide call limit");
    }

    [Test]
    public void PerformanceProfilesShouldApplyQueueDefaults()
    {
        var lowLatency = CreateRuntimeBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)
            .Build();
        var throughput = CreateRuntimeBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build();

        Ensure(lowLatency.Options.FlowControl.MaxSendQueueBytes == 1024 * 1024, "low-latency queue");
        Ensure(throughput.Options.FlowControl.MaxSendQueueBytes == 32 * 1024 * 1024, "throughput queue");
    }

    [Test]
    public void PerformanceProfilesShouldPreserveAnExplicitDefaultValuedQueue()
    {
        const int explicitlyConfiguredQueueBytes = 8 * 1024 * 1024;
        var context = CreateRuntimeBuilder()
            .Configure(options =>
            {
                options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput;
                options.FlowControl.MaxSendQueueBytes = explicitlyConfiguredQueueBytes;
            })
            .Build();

        Ensure(context.Options.FlowControl.MaxSendQueueBytes == explicitlyConfiguredQueueBytes,
            "an explicit queue value must take precedence over a profile default even when it equals the nominal default");
    }

    [Test]
    public void BuiltInCodecShouldBeImmutable()
    {
        try
        {
            CreateRuntimeBuilder().AddCodec(new ReplacementInt32Codec());
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
        var builder = CreateRuntimeBuilder()
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
    public void ContextDisposalShouldDrainAndCloseItsWriterPool()
    {
        var context = CreateRuntimeBuilder()
            .ConfigureBufferPool(options =>
            {
                options.InitialCapacity = 1024;
                options.MaxPooledWriters = 2;
                options.MaxRetainedCapacityBytes = 64 * 1024;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var idle = (PooledByteBufferWriter)context.Buffers.Rent();
        var active = (PooledByteBufferWriter)context.Buffers.Rent();
        _ = idle.GetSpan(32 * 1024);
        _ = active.GetSpan(32 * 1024);
        context.Buffers.Return(idle);

        context.Dispose();
        var retainedAfterDispose = ReadPrivate<int>(context.Buffers, "_pooledCount");
        Exception? rentFailure = null;
        try
        {
            context.Buffers.Rent().Dispose();
        }
        catch (Exception exception)
        {
            rentFailure = exception;
        }
        context.Buffers.Return(active);
        var activeBuffer = ReadPrivate<byte[]?>(active, "_buffer");

        Ensure(retainedAfterDispose == 0, "Context disposal must drain idle writer buffers");
        Ensure(rentFailure is ObjectDisposedException, "a disposed Context pool must reject new rents");
        Ensure(activeBuffer is null, "an active writer returned after Context disposal must release its array");
    }

    [Test]
    public void PendingRequestCapacityShouldHaveAHardMemoryBound()
    {
        const int oversizedCapacity = 2 * 1024 * 1024;
        var options = new SharpLinkProtocolOptions
        {
            MaxPendingRequestsPerConnection = oversizedCapacity
        };

        var optionFailure = CaptureFailure(options.Validate);
        var tableFailure = CaptureFailure(() =>
        {
            using var table = PendingRequestTableTestFixture.Create(oversizedCapacity);
        });

        Ensure(optionFailure is ArgumentOutOfRangeException,
            "public protocol validation must reject an oversized pending table");
        Ensure(tableFailure is ArgumentOutOfRangeException,
            "the internal table constructor must independently enforce the hard bound");
    }

    [Test]
    public void RuntimeSizingShouldRejectUnboundedAggregateMemory()
    {
        var stripeFailure = CaptureFailure(new RuntimeConcurrencyOptions
        {
            StripeCount = 2048,
            InitialMapCapacityPerStripe = 0
        }.Validate);
        var mapCapacityFailure = CaptureFailure(new RuntimeConcurrencyOptions
        {
            StripeCount = 1024,
            InitialMapCapacityPerStripe = 2048
        }.Validate);
        var retainedWriterFailure = CaptureFailure(new BufferWriterPoolOptions
        {
            InitialCapacity = 1024,
            MaxPooledWriters = 2048,
            MaxRetainedCapacityBytes = 64 * 1024
        }.Validate);

        Ensure(stripeFailure is ArgumentOutOfRangeException,
            "stripe objects must have a hard count bound");
        Ensure(mapCapacityFailure is ArgumentOutOfRangeException,
            "aggregate initial map entries must have a hard bound");
        Ensure(retainedWriterFailure is ArgumentOutOfRangeException,
            "aggregate retained writer bytes must have a hard bound");
    }

    [Test]
    public void ServerCallConcurrencyShouldBoundDeadlineScanMemory()
    {
        var failure = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxConcurrentCallsPerConnection = int.MaxValue
        }.Validate);

        Ensure(failure is ArgumentOutOfRangeException,
            "Server call concurrency must bound its per-deadline-scan snapshot");
    }

    [Test]
    public void ServerCallCapacityShouldValidateInclusiveHardRange()
    {
        foreach (var valid in new[]
                 {
                     1,
                     SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer
                 })
        {
            var options = new SharpLinkFlowControlOptions
            {
                MaxConcurrentCallsPerServer = valid
            };

            options.Validate();
            Ensure(options.MaxConcurrentCallsPerServer == valid,
                $"server-wide call capacity boundary {valid}");
        }

        foreach (var invalid in new[]
                 {
                     0,
                     SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer + 1
                 })
        {
            var failure = CaptureFailure(new SharpLinkFlowControlOptions
            {
                MaxConcurrentCallsPerServer = invalid
            }.Validate);

            Ensure(failure is ArgumentOutOfRangeException
            {
                ParamName: nameof(SharpLinkFlowControlOptions.MaxConcurrentCallsPerServer)
            },
                $"server-wide call capacity {invalid} must fail its own public validation");
        }
    }

    [Test]
    public void ConnectionAndServerCallCapacitySnapshotsShouldRemainIndependent()
    {
        var builder = CreateRuntimeBuilder()
            .Configure(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 7;
                options.FlowControl.MaxConcurrentCallsPerServer = 11;
            });
        using var first = builder.Build();

        builder.Configure(options =>
        {
            options.FlowControl.MaxConcurrentCallsPerConnection = 13;
            options.FlowControl.MaxConcurrentCallsPerServer = 17;
        });
        using var second = builder.Build();

        var leakedFirstCopy = first.Options;
        leakedFirstCopy.FlowControl.MaxConcurrentCallsPerConnection = 19;
        leakedFirstCopy.FlowControl.MaxConcurrentCallsPerServer = 23;

        Ensure(first.Options.FlowControl.MaxConcurrentCallsPerConnection == 7,
            "first per-connection call-capacity snapshot");
        Ensure(first.Options.FlowControl.MaxConcurrentCallsPerServer == 11,
            "first server-wide call-capacity snapshot");
        Ensure(second.Options.FlowControl.MaxConcurrentCallsPerConnection == 13,
            "second per-connection call-capacity snapshot");
        Ensure(second.Options.FlowControl.MaxConcurrentCallsPerServer == 17,
            "second server-wide call-capacity snapshot");
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
                return CreateRuntimeBuilder()
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
    private sealed class AdapterValue;
    private sealed class SecondAdapterValue;
    private sealed class ThirdAdapterValue;

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
        public RpcHash128 CodecHash => new(0x636174616c6f672dUL, 0x636f6465632d7631UL);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new CatalogCodec()
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<CatalogValue>;
    }

    private sealed class CatalogManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(CatalogManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => "catalog-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new CatalogCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    public sealed class CountingAdapter : IRpcCodecAdapter
    {
        internal const string Id = "test.adapter/v1";
        internal const string Wire = "test-wire/v1";
        private readonly AdapterCounters _counters;

        public CountingAdapter()
            : this(new AdapterCounters())
        {
        }

        internal CountingAdapter(AdapterCounters counters) => _counters = counters;

        public string AdapterId => Id;

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _counters.ScopeCreateCount);
            return new CountingScope(_counters);
        }
    }

    public sealed class AlternateCountingAdapter : IRpcCodecAdapter
    {
        internal const string Id = "test.alternate-adapter/v1";
        internal const string Wire = "test-alternate-wire/v1";
        private readonly AdapterCounters _counters;

        public AlternateCountingAdapter()
            : this(new AdapterCounters())
        {
        }

        internal AlternateCountingAdapter(AdapterCounters counters) => _counters = counters;

        public string AdapterId => Id;

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _counters.ScopeCreateCount);
            return new CountingScope(_counters);
        }
    }

    public sealed class InstanceIdentityAdapter : IRpcCodecAdapter
    {
        internal const string Id = "test.instance-identity/v1";
        internal const string Wire = CountingAdapter.Wire;
        private readonly AdapterCounters _counters;
        private readonly string _adapterId;
        private readonly string _wireFormatId;

        public InstanceIdentityAdapter()
            : this(new AdapterCounters(), Id, Wire)
        {
        }

        internal InstanceIdentityAdapter(
            AdapterCounters counters,
            string adapterId,
            string wireFormatId)
        {
            _counters = counters;
            _adapterId = adapterId;
            _wireFormatId = wireFormatId;
        }

        public string AdapterId => _adapterId;
        public string WireFormatId => _wireFormatId;

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _counters.ScopeCreateCount);
            return new CountingScope(_counters);
        }
    }

    public sealed class ThrowingDisposeAdapter : IRpcCodecAdapter
    {
        internal const string Id = "z.test.throwing-dispose/v1";
        internal const string Wire = "test-throwing-dispose-wire/v1";
        private readonly AdapterCounters _counters;

        public ThrowingDisposeAdapter()
            : this(new AdapterCounters())
        {
        }

        internal ThrowingDisposeAdapter(AdapterCounters counters) => _counters = counters;

        public string AdapterId => Id;

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _counters.ScopeCreateCount);
            return new ThrowingDisposeScope(_counters);
        }
    }

    public sealed class FailingScopeAdapter : IRpcCodecAdapter
    {
        internal const string Id = "z.test.failing-adapter/v1";
        internal const string Wire = "test-failing-wire/v1";
        private readonly AdapterCounters _counters;
        private readonly bool _returnNull;

        public FailingScopeAdapter()
            : this(new AdapterCounters(), returnNull: false)
        {
        }

        internal FailingScopeAdapter(AdapterCounters counters, bool returnNull)
        {
            _counters = counters;
            _returnNull = returnNull;
        }

        public string AdapterId => Id;

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _counters.ScopeCreateCount);
            if (_returnNull)
                return null!;
            throw new InvalidOperationException("scope failure");
        }
    }

    private sealed class NamedThrowingDisposeAdapter(string adapterId, string message) : IRpcCodecAdapter
    {
        public string AdapterId => adapterId;
        public string WireFormatId => "throwing-wire/v1";
        public IRpcCodecAdapterScope CreateScope() => new NamedThrowingDisposeScope(message);
    }

    private sealed class NamedThrowingDisposeScope(string message) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>() => new AdapterCodec<T>();
        public void Dispose() => throw new InvalidOperationException(message);
    }

    private sealed class CountingScope(AdapterCounters counters) : IRpcCodecAdapterScope
    {
        private int _disposed;

        public IRpcCodec<T> CreateCodec<
            [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
                System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var number = Interlocked.Increment(ref counters.CodecCreateCount);
            if (number == Volatile.Read(ref counters.FailOnCodecNumber))
                throw new InvalidOperationException("candidate failure");
            return new AdapterCodec<T>();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref counters.ScopeDisposeCount);
        }
    }

    private sealed class ThrowingDisposeScope(AdapterCounters counters) : IRpcCodecAdapterScope
    {
        private int _disposed;

        public IRpcCodec<T> CreateCodec<
            [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
                System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Interlocked.Increment(ref counters.CodecCreateCount);
            return new AdapterCodec<T>();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Increment(ref counters.ScopeDisposeCount);
            throw new InvalidOperationException("scope dispose failure");
        }
    }

    private sealed class AdapterCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
        {
        }

        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private sealed class TaggedAdapterValueCodec(int tag) : IRpcCodec<AdapterValue>
    {
        internal int Tag { get; } = tag;

        public void Serialize(in AdapterValue value, IBufferWriter<byte> buffer)
        {
        }

        public AdapterValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class TaggedThirdAdapterValueCodec(int tag) : IRpcCodec<ThirdAdapterValue>
    {
        internal int Tag { get; } = tag;

        public void Serialize(in ThirdAdapterValue value, IBufferWriter<byte> buffer)
        {
        }

        public ThirdAdapterValue Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class HashedNativeFactory<T>(IRpcCodec<T> codec, RpcHash128 codecHash) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => codecHash;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? codec
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }

    private sealed class FixedNativeFactory<T>(IRpcCodec<T> codec) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x66697865642d6e61UL, 0x746976652d763031UL);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? codec
                : throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }

    private sealed class CustomWireFactory<T>(IRpcCodec<T> codec, string wireFormatId) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x637573746f6d2d63UL, 0x6f6465632d763031UL);
        public string WireFormatId => wireFormatId;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? codec
                : throw new ArgumentException("Adapter-free custom Codec does not accept an Adapter Scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }

    private sealed class BlockingNativeFactory<T>(
        IRpcCodec<T> codec,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x626c6f636b696e67UL, 0x2d636f6465632d31UL);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            if (adapterScope is not null)
                throw new ArgumentException("Native factory does not accept an Adapter Scope.", nameof(adapterScope));
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return codec;
        }

        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<T>;
    }

    private sealed class AdapterFactory<T>(AdapterCounters counters) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x616461707465722dUL, 0x636f6465632d7631UL);
        public string? AdapterId => "test.adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = new CountingAdapter(counters);

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<T>();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class ConfigurableAdapterFactory<T> : IRpcGeneratedCodecFactory
    {
        private readonly IRpcCodec? _codec;

        internal ConfigurableAdapterFactory(
            IRpcCodecAdapter adapter,
            string adapterId,
            string wireFormatId,
            IRpcCodec? codec = null,
            RpcHash128 codecHash = default)
        {
            Adapter = adapter;
            AdapterId = adapterId;
            CodecHash = codecHash.IsEmpty
                ? new RpcHash128(0x636f6e6669672d61UL, 0x6461707465722d31UL)
                : codecHash;
            _codec = codec;
        }

        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash { get; }
        public string AdapterId { get; }
        public IRpcCodecAdapter Adapter { get; }

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => _codec ?? (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<T>();

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class AdapterManifest(AdapterCounters counters, bool includeSecondCodec) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(AdapterManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => "adapter-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = includeSecondCodec
            ? [new AdapterFactory<AdapterValue>(counters), new AdapterFactory<SecondAdapterValue>(counters)]
            : [new AdapterFactory<AdapterValue>(counters)];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class TestManifest(string descriptor, params IRpcGeneratedCodecFactory[] codecs)
        : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(TestManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => TestAssemblyHash;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = codecs;
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class CallerOwnedAdapterValueCodec : IRpcCodec<AdapterValue>, IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Serialize(in AdapterValue value, IBufferWriter<byte> buffer)
        {
        }

        public AdapterValue Deserialize(in ReadOnlySequence<byte> buffer) => new();

        public void Dispose() => DisposeCount++;
    }

    internal sealed class AdapterCounters
    {
        internal int ScopeCreateCount;
        internal int CodecCreateCount;
        internal int ScopeDisposeCount;
        internal int FailOnCodecNumber;
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static SharpLinkRuntimeContextBuilder CreateRuntimeBuilder() =>
        new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);

    private static T ReadPrivate<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {fieldName}");
        return (T)field.GetValue(instance)!;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
