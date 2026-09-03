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

    [Test]
    public void AdapterTypesInOneManifestShouldShareOneScopeAndDisposeWithContext()
    {
        var counters = new AdapterCounters();
        using (var context = CreateRuntimeBuilder()
                   .Build([new AdapterManifest(counters, includeSecondCodec: true)]))
        {
            Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
                "first Adapter Codec");
            Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
                "second Adapter Codec");
            Ensure(counters.ScopeCreateCount == 1,
                "one Manifest and Adapter ID must create one Scope");
            Ensure(counters.CodecCreateCount == 2,
                "both closed Codecs are prepared transactionally");
            Ensure(counters.ScopeDisposeCount == 0, "scope remains live with Context");
        }
        Ensure(counters.ScopeDisposeCount == 1, "Context disposes Adapter Scope once");
    }

    [Test]
    public void SeparateContextsAndManifestsShouldOwnSeparateAdapterScopes()
    {
        var counters = new AdapterCounters();
        var manifest = new AdapterManifest(counters, includeSecondCodec: false);
        using var first = CreateRuntimeBuilder().Build([manifest]);
        using var second = CreateRuntimeBuilder().Build([manifest]);
        Ensure(counters.ScopeCreateCount == 2,
            "same Manifest in two Runtime Contexts must use separate Scopes");
    }

    [Test]
    public void DifferentManifestsInOneContextShouldOwnSeparateAdapterScopes()
    {
        var counters = new AdapterCounters();
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest("first", new AdapterFactory<AdapterValue>(counters)),
            new TestManifest("second", new AdapterFactory<SecondAdapterValue>(counters))
        ]);

        Ensure(counters.ScopeCreateCount == 2,
            "the same Adapter ID in two Manifest instances must create separate Scopes");
        Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
            "first Manifest Codec");
        Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
            "second Manifest Codec");
    }

    [Test]
    public void DifferentAdaptersInOneManifestShouldOwnSeparateScopes()
    {
        var firstCounters = new AdapterCounters();
        var secondCounters = new AdapterCounters();
        using var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "two-adapters",
                new AdapterFactory<AdapterValue>(firstCounters),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new AlternateCountingAdapter(secondCounters),
                    AlternateCountingAdapter.Id,
                    AlternateCountingAdapter.Wire))
        ]);

        Ensure(firstCounters.ScopeCreateCount == 1, "first Adapter owns one Scope");
        Ensure(secondCounters.ScopeCreateCount == 1, "second Adapter owns one Scope");
        Ensure(context.Codecs.GetCodec<AdapterValue>() is AdapterCodec<AdapterValue>,
            "first Adapter Codec");
        Ensure(context.Codecs.GetCodec<SecondAdapterValue>() is AdapterCodec<SecondAdapterValue>,
            "second Adapter Codec");
    }

    [Test]
    public void FailedAdapterCodecPreparationShouldDisposeCandidateScope()
    {
        var counters = new AdapterCounters { FailOnCodecNumber = 2 };
        try
        {
            using var _ = CreateRuntimeBuilder()
                .Build([new AdapterManifest(counters, includeSecondCodec: true)]);
            throw new Exception("expected second Adapter Codec creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("candidate failure", StringComparison.Ordinal),
                "candidate failure is preserved");
        }
        Ensure(counters.ScopeDisposeCount == 1,
            "failed transaction disposes the candidate Scope");
    }

    [Test]
    public void ThirdAdapterCodecFailureShouldDisposeCandidateScope()
    {
        var counters = new AdapterCounters { FailOnCodecNumber = 3 };
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "third-codec-failure",
                    new AdapterFactory<AdapterValue>(counters),
                    new AdapterFactory<SecondAdapterValue>(counters),
                    new AdapterFactory<ThirdAdapterValue>(counters))
            ]);
            throw new Exception("expected third Adapter Codec creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("candidate failure", StringComparison.Ordinal),
                "third candidate failure is preserved");
        }

        Ensure(counters.CodecCreateCount == 3, "the third closed Codec triggers the failure");
        Ensure(counters.ScopeDisposeCount == 1,
            "the shared candidate Scope is disposed after the third Codec fails");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ScopeCreationFailureShouldRollbackEarlierScopes(bool returnNull)
    {
        var preparedCounters = new AdapterCounters();
        var failingCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "scope-failure",
                    new AdapterFactory<AdapterValue>(preparedCounters),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new FailingScopeAdapter(failingCounters, returnNull),
                        FailingScopeAdapter.Id,
                        FailingScopeAdapter.Wire))
            ]);
            throw new Exception("expected Adapter Scope creation to fail");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains(returnNull ? "null scope" : "scope failure", StringComparison.Ordinal),
                "Scope failure reason is preserved");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the first Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the first Scope was rolled back");
        Ensure(failingCounters.ScopeCreateCount == 1, "the failing Adapter was invoked exactly once");
    }

    [Test]
    public void ManifestPreparationRollbackShouldPreservePrimaryAndScopeCleanupFailures()
    {
        var failure = CaptureFailure(() =>
        {
            using var context = CreateRuntimeBuilder()
                .Build(includeGeneratedAssemblyCatalog: false);
            _ = context.PrepareGeneratedManifest(new TestManifest(
                "manifest-rollback-failure",
                new ConfigurableAdapterFactory<AdapterValue>(
                    new NamedThrowingDisposeAdapter("a.throwing/v1", "candidate scope cleanup failed"),
                    "a.throwing/v1",
                    "throwing-wire/v1"),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new FailingScopeAdapter(new AdapterCounters(), returnNull: false),
                    FailingScopeAdapter.Id,
                    FailingScopeAdapter.Wire)));
        });

        Ensure(ContainsMessage(failure, "scope failure"),
            "Manifest rollback must retain the Scope creation failure");
        Ensure(ContainsMessage(failure, "candidate scope cleanup failed"),
            "Manifest rollback must retain earlier Scope cleanup failure");
    }

    [Test]
    public void ContextConstructionRollbackShouldPreserveManifestAndCleanupFailures()
    {
        var failure = CaptureFailure(() => _ = CreateRuntimeBuilder().Build([
            new TestManifest(
                "prepared-throwing-manifest",
                new ConfigurableAdapterFactory<AdapterValue>(
                    new NamedThrowingDisposeAdapter("a.prepared/v1", "prepared manifest cleanup failed"),
                    "a.prepared/v1",
                    "throwing-wire/v1")),
            new TestManifest(
                "failing-manifest",
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new FailingScopeAdapter(new AdapterCounters(), returnNull: false),
                    FailingScopeAdapter.Id,
                    FailingScopeAdapter.Wire))
        ]));

        Ensure(ContainsMessage(failure, "scope failure"),
            "Context rollback must retain the later Manifest failure");
        Ensure(ContainsMessage(failure, "prepared manifest cleanup failed"),
            "Context rollback must retain prepared Manifest cleanup failure");
    }

    [Test]
    public void AdapterIdentityMismatchShouldRejectAndDisposePreparedScopes()
    {
        var preparedCounters = new AdapterCounters();
        var mismatchedCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "identity-mismatch",
                    new AdapterFactory<AdapterValue>(preparedCounters),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new CountingAdapter(mismatchedCounters),
                        "z.test.adapter/v1",
                        "test-wire/v1"))
            ]);
            throw new Exception("expected Adapter identity mismatch");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("lifecycle identity", StringComparison.Ordinal),
                "identity mismatch is reported before publication");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the earlier valid Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the earlier valid Scope was rolled back");
        Ensure(mismatchedCounters.ScopeCreateCount == 0,
            "an Adapter with mismatched identity cannot create a Scope");
    }

    [Test]
    public void EveryFactoryAdapterInstanceShouldMatchGeneratedIdentity()
    {
        var preparedCounters = new AdapterCounters();
        var mismatchedCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "per-factory-identity",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new InstanceIdentityAdapter(
                            preparedCounters,
                            InstanceIdentityAdapter.Id,
                            InstanceIdentityAdapter.Wire),
                        InstanceIdentityAdapter.Id,
                        InstanceIdentityAdapter.Wire),
                    new ConfigurableAdapterFactory<SecondAdapterValue>(
                        new InstanceIdentityAdapter(
                            mismatchedCounters,
                            "mismatched-instance/v1",
                            InstanceIdentityAdapter.Wire),
                        InstanceIdentityAdapter.Id,
                        InstanceIdentityAdapter.Wire))
            ]);
            throw new Exception("expected every factory Adapter instance to be validated");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("lifecycle identity", StringComparison.Ordinal),
                "a later same-type Adapter instance cannot bypass generated AdapterId validation");
        }

        Ensure(preparedCounters.ScopeCreateCount == 1, "the first valid Adapter Scope was prepared");
        Ensure(preparedCounters.ScopeDisposeCount == 1, "the prepared Scope was rolled back");
        Ensure(mismatchedCounters.ScopeCreateCount == 0,
            "the mismatched later Adapter instance cannot create a Scope");
    }

    [Test]
    public void WrongTypedCodecShouldRejectAndDisposeCandidateScope()
    {
        var counters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest(
                    "wrong-codec",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new CountingAdapter(counters),
                        CountingAdapter.Id,
                        CountingAdapter.Wire,
                        new CatalogCodec()))
            ]);
            throw new Exception("expected an incompatible Codec to be rejected");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("incompatible IRpcCodec", StringComparison.Ordinal),
                "wrong closed Codec type is rejected before publication");
        }

        Ensure(counters.ScopeCreateCount == 1, "candidate Scope was created");
        Ensure(counters.ScopeDisposeCount == 1, "candidate Scope was rolled back");
    }

    [Test]
    public void ExplicitCodecShouldWinAndRemainCallerOwned()
    {
        var counters = new AdapterCounters();
        var explicitCodec = new CallerOwnedAdapterValueCodec();
        var context = CreateRuntimeBuilder()
            .AddCodec(explicitCodec)
            .Build([new AdapterManifest(counters, includeSecondCodec: false)]);

        Ensure(ReferenceEquals(context.Codecs.GetCodec<AdapterValue>(), explicitCodec),
            "explicit UseCodec registration wins over generated Adapter Codec");
        context.Dispose();
        context.Dispose();

        Ensure(counters.ScopeDisposeCount == 1, "Context-owned Adapter Scope is disposed once");
        Ensure(explicitCodec.DisposeCount == 0, "caller-owned explicit Codec is not disposed by Runtime");
    }

    [Test]
    public void ConflictingManifestCodecsShouldRollbackBothAdapterScopes()
    {
        var firstCounters = new AdapterCounters();
        var secondCounters = new AdapterCounters();
        try
        {
            using var _ = CreateRuntimeBuilder().Build([
                new TestManifest("first-conflict", new AdapterFactory<AdapterValue>(firstCounters)),
                new TestManifest(
                    "second-conflict",
                    new ConfigurableAdapterFactory<AdapterValue>(
                        new AlternateCountingAdapter(secondCounters),
                        AlternateCountingAdapter.Id,
                        AlternateCountingAdapter.Wire,
                        codecHash: new RpcHash128(
                            0x636f6e666c696374UL,
                            0x2d636f6465632d32UL)))
            ]);
            throw new Exception("expected generated Codec conflict");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("Generated Codec conflict", StringComparison.Ordinal),
                "same-target CodecHash conflict is rejected");
        }

        Ensure(firstCounters.ScopeDisposeCount == 1, "first Manifest Scope is rolled back");
        Ensure(secondCounters.ScopeDisposeCount == 1, "second Manifest Scope is rolled back");
    }

    [Test]
    public void ScopeDisposeFailureShouldNotSkipRemainingAdapterScopes()
    {
        var remainingCounters = new AdapterCounters();
        var throwingCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "dispose-failure",
                new AdapterFactory<AdapterValue>(remainingCounters),
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new ThrowingDisposeAdapter(throwingCounters),
                    ThrowingDisposeAdapter.Id,
                    ThrowingDisposeAdapter.Wire))
        ]);

        try
        {
            context.Dispose();
            throw new Exception("expected Scope disposal failure to be reported");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("scope dispose failure", StringComparison.Ordinal),
                "original Scope disposal failure is preserved");
        }

        Ensure(throwingCounters.ScopeDisposeCount == 1, "throwing Scope is attempted once");
        Ensure(remainingCounters.ScopeDisposeCount == 1,
            "remaining Scope is disposed even after another Scope throws");
    }

    [Test]
    public void ContextDisposeFailureShouldNotSkipRemainingManifestRegistrations()
    {
        var remainingCounters = new AdapterCounters();
        var throwingCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build([
            new TestManifest(
                "remaining-registration",
                new AdapterFactory<AdapterValue>(remainingCounters)),
            new TestManifest(
                "throwing-registration",
                new ConfigurableAdapterFactory<SecondAdapterValue>(
                    new ThrowingDisposeAdapter(throwingCounters),
                    ThrowingDisposeAdapter.Id,
                    ThrowingDisposeAdapter.Wire))
        ]);

        try
        {
            context.Dispose();
            throw new Exception("expected Scope disposal failure to be reported");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("scope dispose failure", StringComparison.Ordinal),
                "first disposal failure is preserved across Manifest cleanup");
        }

        Ensure(throwingCounters.ScopeDisposeCount == 1, "throwing registration is attempted once");
        Ensure(remainingCounters.ScopeDisposeCount == 1,
            "remaining Manifest registration is disposed after another registration throws");
    }

    [Test]
    public void ContextDisposeShouldPreserveEveryAdapterScopeFailure()
    {
        var context = CreateRuntimeBuilder().Build([
            new TestManifest("first-throw", new ConfigurableAdapterFactory<AdapterValue>(
                new NamedThrowingDisposeAdapter("throwing.first/v1", "first scope cleanup failed"),
                "throwing.first/v1", "throwing-wire/v1")),
            new TestManifest("second-throw", new ConfigurableAdapterFactory<SecondAdapterValue>(
                new NamedThrowingDisposeAdapter("throwing.second/v1", "second scope cleanup failed"),
                "throwing.second/v1", "throwing-wire/v1"))
        ]);

        Exception failure;
        try
        {
            context.Dispose();
            throw new Exception("expected Adapter scope cleanup failures");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsMessage(failure, "first scope cleanup failed"), "first scope failure retained");
        Ensure(ContainsMessage(failure, "second scope cleanup failed"), "second scope failure retained");
    }

    [Test]
    public async Task TenThousandCodecPublicationRacesShouldPreserveRegistrationIdentity()
    {
        var oldCounters = new AdapterCounters();
        var newCounters = new AdapterCounters();
        var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var oldRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "old-generation",
            new ConfigurableAdapterFactory<AdapterValue>(
                new CountingAdapter(oldCounters),
                CountingAdapter.Id,
                CountingAdapter.Wire,
                new TaggedAdapterValueCodec(1))));
        var newRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "new-generation",
            new ConfigurableAdapterFactory<AdapterValue>(
                new CountingAdapter(newCounters),
                CountingAdapter.Id,
                CountingAdapter.Wire,
                new TaggedAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(oldRegistration);
        context.AdoptGeneratedManifest(newRegistration);
        context.PublishGeneratedCodecs(oldRegistration.Codecs);

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var next = iteration % 2 == 0 ? newRegistration : oldRegistration;
            var expectedTag = iteration % 2 == 0 ? 2 : 1;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racedLookup = Task.Run(() =>
            {
                ready.SetResult();
                return context.Codecs.GetCodec<AdapterValue>();
            });
            await ready.Task;
            context.PublishGeneratedCodecs(next.Codecs);
            var racedCodec = await racedLookup;
            Ensure(racedCodec is TaggedAdapterValueCodec { Tag: 1 or 2 },
                $"raced lookup {iteration} returns a complete published generation");
            Ensure(context.Codecs.GetCodec<AdapterValue>() is TaggedAdapterValueCodec { Tag: var tag } &&
                   tag == expectedTag,
                $"post-publication lookup {iteration} uses the current registration");
        }

        context.PublishGeneratedCodecs(newRegistration.Codecs);
        context.ReleaseGeneratedManifest(oldRegistration);
        Ensure(context.Codecs.GetCodec<AdapterValue>() is TaggedAdapterValueCodec { Tag: 2 },
            "old owner cleanup cannot evict the replacement Codec");
        Ensure(oldCounters.ScopeDisposeCount == 1, "old generation Scope is disposed exactly once");
        Ensure(newCounters.ScopeDisposeCount == 0, "new generation Scope remains active");
        context.Dispose();
        context.Dispose();
        Ensure(newCounters.ScopeDisposeCount == 1, "new generation Scope is disposed exactly once");
    }

    [Test]
    public async Task GeneratedCodecResolutionCrossingPublicationShouldUseCurrentGeneration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var oldRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "blocking-old-generation",
            new BlockingNativeFactory<ThirdAdapterValue>(
                new TaggedThirdAdapterValueCodec(1), entered, release)));
        var newRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "new-generation",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(oldRegistration);
        context.AdoptGeneratedManifest(newRegistration);
        context.PublishGeneratedCodecs(oldRegistration.Codecs);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(newRegistration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a Codec resolution returning after publication must use the current generation");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }

    [Test]
    public async Task FallbackCodecResolutionCrossingPublicationShouldUseGeneratedCodec()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new TaggedThirdAdapterValueCodec(1);
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "generated-during-fallback",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(registration);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(registration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a fallback resolution must not cross a generated publication boundary");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }

    [Test]
    public async Task NullFallbackResolutionCrossingPublicationShouldUseGeneratedCodec()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return null;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "generated-during-null-fallback",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(2))));
        context.AdoptGeneratedManifest(registration);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.PublishGeneratedCodecs(registration.Codecs);
            release.TrySetResult();

            var resolved = await racedLookup.WaitAsync(RaceCoordinationTimeout);
            Ensure(resolved is TaggedThirdAdapterValueCodec { Tag: 2 },
                "a null fallback result must recheck generated publication");
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
        }
    }

    [Test]
    public async Task CodecResolutionCrossingContextDisposalShouldFail()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new TaggedThirdAdapterValueCodec(1);
            })
            .Build(includeGeneratedAssemblyCatalog: false);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.Dispose();
            release.TrySetResult();

            try
            {
                _ = await racedLookup.WaitAsync(RaceCoordinationTimeout);
                throw new Exception("expected in-flight Codec resolution to observe Context disposal");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
            context.Dispose();
        }
    }

    [Test]
    public async Task NullCodecResolutionCrossingContextDisposalShouldFailAsDisposed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = CreateRuntimeBuilder()
            .UseCodecResolver(type =>
            {
                if (type != typeof(ThirdAdapterValue))
                    return null;
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return null;
            })
            .Build(includeGeneratedAssemblyCatalog: false);

        var racedLookup = LongRunningTestWorker.Run(
            () => context.Codecs.GetCodec<ThirdAdapterValue>());
        try
        {
            await entered.Task.WaitAsync(RaceCoordinationTimeout);
            context.Dispose();
            release.TrySetResult();

            try
            {
                _ = await racedLookup.WaitAsync(RaceCoordinationTimeout);
                throw new Exception("expected null Codec resolution to observe Context disposal");
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            release.TrySetResult();
            await LongRunningTestWorker.JoinAsync(racedLookup, RaceCoordinationTimeout);
            context.Dispose();
        }
    }

    [Test]
    public void UnchangedCodecShouldRefreshAcrossAnUnrelatedSnapshotRemoval()
    {
        using var context = CreateRuntimeBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var stableRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "stable-codec",
            new FixedNativeFactory<ThirdAdapterValue>(new TaggedThirdAdapterValueCodec(3))));
        var removedRegistration = context.PrepareGeneratedManifest(new TestManifest(
            "removed-codec",
            new FixedNativeFactory<SecondAdapterValue>(new AdapterCodec<SecondAdapterValue>())));
        context.AdoptGeneratedManifest(stableRegistration);
        context.AdoptGeneratedManifest(removedRegistration);
        var combined = stableRegistration.Codecs
            .Concat(removedRegistration.Codecs)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        context.PublishGeneratedCodecs(combined);
        var before = context.Codecs.GetCodec<ThirdAdapterValue>();

        context.PublishGeneratedCodecs(stableRegistration.Codecs);
        var after = context.Codecs.GetCodec<ThirdAdapterValue>();

        Ensure(ReferenceEquals(before, after),
            "an unchanged registration refreshes its snapshot identity without recreating its Codec");
        context.ReleaseGeneratedManifest(removedRegistration);
    }

    [Test]
    public void AdapterFreeCustomWireCodecShouldBeAccepted()
    {
        using var context = CreateRuntimeBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var registration = context.PrepareGeneratedManifest(new TestManifest(
            "custom-codec",
            new CustomWireFactory<ThirdAdapterValue>(
                new TaggedThirdAdapterValueCodec(7),
                "custom-wire/v1")));
        context.AdoptGeneratedManifest(registration);
        context.PublishGeneratedCodecs(registration.Codecs);

        Ensure(context.Codecs.GetCodec<ThirdAdapterValue>() is TaggedThirdAdapterValueCodec { Tag: 7 },
            "an adapter-free Codec with a custom deterministic identity must resolve through the generated registration");
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
