using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterDynamicAssemblyTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task DynamicRegistrationShouldPreserveStructuredNullAndMissingUnregisterResults()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        var nullRegistration = client.RegisterAssembly("plugins", null!);
        Ensure(!nullRegistration.Succeeded &&
               nullRegistration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null dynamic registration must return the shared structured invalid-argument result");

        var missingUnregister = await client.UnregisterAssemblyAsync(
            "plugins", typeof(string).Assembly, TimeSpan.Zero);
        Ensure(!missingUnregister.ReferencesReleased,
            "unregistering an assembly that is not registered must match child false-result semantics");
    }

    [Test]
    public async Task DynamicRegistrationShouldReturnStructuredFailureAfterStop()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var registration = client.RegisterAssembly("plugins", typeof(string).Assembly);
        Ensure(!registration.Succeeded &&
               registration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
            "registration after shutdown must return the structured terminal-state failure before cluster lookup");
    }

    [Test]
    public async Task DynamicReplacementShouldReturnStructuredFailureAfterStop()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var replacement = await client.ReplaceAssemblyAsync(
            "plugins", typeof(string).Assembly, typeof(int).Assembly, TimeSpan.Zero);
        Ensure(!replacement.Succeeded &&
               replacement.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
            "replacement after shutdown must return the structured terminal-state failure before cluster lookup");
    }

    [Test]
    public async Task DynamicUnregisterShouldReturnFalseAfterStop()
    {
        await using var client = CreateDynamicBuilder()
            .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        await client.StopAsync();
        var unregister = await client.UnregisterAssemblyAsync(
            "plugins", typeof(string).Assembly, TimeSpan.Zero);
        Ensure(!unregister.ReferencesReleased,
            "unregistration after shutdown must return the child-compatible false result before cluster lookup");
    }

    [Test]
    public async Task ConcurrentDynamicUnregisterShouldShareOneCoordinatorOperation()
    {
        var child = new CoordinatedUnregisterClient();
        SharpLinkClusterKey cluster = "plugins";
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var route = new SharpLinkClusterRouteRegistration(
            typeof(IOrdersContract),
            8_101,
            "orders-v1",
            slot,
            TestManifestAssembly);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        AddPreparedUnregisterFixture(client, slot, route);

        var first = client.UnregisterAssemblyAsync(
            cluster, TestManifestAssembly, TimeSpan.Zero).AsTask();
        var second = client.UnregisterAssemblyAsync(
            cluster, TestManifestAssembly, TimeSpan.Zero).AsTask();
        var rejectedTransport = new ControlledMutationTransportFactory();
        var replacementResult = await client.ReplaceClusterAsync(
            cluster,
            childBuilder => childBuilder.DisableRequestTimeout().UseTransport(rejectedTransport),
            TimeSpan.Zero);
        child.RejectUnregister(new InvalidOperationException("controlled child unregister failed"));
        var firstFailure = await CaptureExceptionAsync(first);
        var secondFailure = await CaptureExceptionAsync(second);

        Ensure(replacementResult is
        {
            Succeeded: false,
            Published: false,
            FailureCode: SharpLinkClusterMutationFailureCode.Busy
        },
            "slot replacement must return Busy while assembly unregister/drain owns the generation");
        Ensure(rejectedTransport.DisposeCount == 1,
            "assembly-lifecycle rejection must dispose the unbuilt replacement transport");
        Ensure(child.UnregisterCallCount == 1,
            "concurrent coordinator callers must invoke the child unregister operation once");
        Ensure(ReferenceEquals(firstFailure, secondFailure),
            "concurrent coordinator callers must observe the same original failure");
        Ensure(firstFailure is InvalidOperationException { Message: "controlled child unregister failed" },
            "the shared operation must preserve the child failure");
    }

    [Test]
    // The rollback plugin exposes a process-wide environment switch and disposal state.
    [NotInParallel("rollback-plugin")]
    public async Task RuntimeReplaceShouldMigrateDynamicAssemblyBeforeSwitchingRoute(CancellationToken testCancellationToken)
    {
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        try
        {
            var oldTransport = new ControlledMutationTransportFactory();
            var replacementTransport = new ControlledMutationTransportFactory();
            await using var client = CreateStaticBuilder()
                .AddCluster("orders", child => child.UseTransport(oldTransport),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            var dynamicAssembly = typeof(RollbackMarker).Assembly;
            var registration = client.RegisterAssembly("orders", dynamicAssembly);
            Ensure(registration.Succeeded, $"dynamic setup registration must succeed: {registration.Error}");
            var oldProxy = (OrdersProxy)client.CreateProxy<IOrdersContract>().Service;

            await client.ReplaceClusterAsync(
                "orders",
                child => child.UseTransport(replacementTransport),
                TimeSpan.FromSeconds(2));
            var replacementProxy = (OrdersProxy)client.CreateProxy<IOrdersContract>().Service;
            var duplicate = client.RegisterAssembly("orders", dynamicAssembly);
            var coordinator = (SharpLinkMultiClusterClient)client;
            var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
                .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator)!;
            var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator)!;

            Ensure(!ReferenceEquals(oldProxy.Channel, replacementProxy.Channel),
                "the static route must switch to the replacement child after dynamic migration succeeds");
            Ensure(registrations.Count == 1 &&
                   ReferenceEquals(registrations[0].Slot, snapshot.Clusters["orders"]) &&
                   ReferenceEquals(registrations[0].Assembly, dynamicAssembly),
                "the coordinator must retarget its dynamic registration to the replacement snapshot slot");
            Ensure(!duplicate.Succeeded &&
                   duplicate.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                "the coordinator dynamic registration catalog must migrate with the replacement slot");
            Ensure(oldTransport.DisposeCount == 1,
                "the old dynamically registered child must retire after migration");
            Ensure(replacementTransport.DisposeCount == 0,
                "the replacement child must remain owned by the coordinator");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    // The rollback plugin exposes process-wide construction gates and environment switches.
    [NotInParallel("rollback-plugin")]
    public async Task DynamicRegistrationShouldRejectASlotChangedWhileItsManifestLoads(CancellationToken testCancellationToken)
    {
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        var manifestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RollbackState.ManifestConstructionStarted = manifestStarted;
        RollbackState.ManifestConstructionRelease = manifestRelease;
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
        SharpLinkClusterKey cluster = "plugins";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        try
        {
            var registration = LongRunningTestWorker.Run(() =>
                client.RegisterAssembly(cluster, typeof(RollbackMarker).Assembly));
            await manifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var removal = await client.RemoveClusterAsync(cluster, TimeSpan.Zero);
            Ensure(removal is { Succeeded: true, ForcedStop: true },
                "the concurrent remove must publish while manifest loading is paused");
            manifestRelease.TrySetResult();

            var result = await registration.WaitAsync(TimeSpan.FromSeconds(2));
            var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            Ensure(!result.Succeeded &&
                   result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "registration must reject a slot that changed while its manifest loaded");
            Ensure(child.RegisterAssemblyCallCount == 0 && registrations.Count == 0,
                "registration must not reach or retain the retired child");

            child.ReleaseStop();
            await client.StopAsync();
        }
        finally
        {
            manifestRelease.TrySetResult();
            child.ReleaseStop();
            RollbackState.ManifestConstructionStarted = null;
            RollbackState.ManifestConstructionRelease = null;
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task RuntimeReplaceDynamicMigrationFailureShouldKeepOldSlotAndRoute()
    {
        var oldTransport = new ControlledMutationTransportFactory();
        var rejectedTransport = new ControlledMutationTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(oldTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        var migration = new MigrationCodecState();
        AddPreparedMigrationFixture((SharpLinkMultiClusterClient)client, migration);
        Ensure(migration.ScopeCreateCount == 0 && migration.FactoryCreateCount == 0,
            "capturing owned metadata must not materialize the candidate child's Codec");
        var oldProxy = (OrdersProxy)client.CreateProxy<IOrdersContract>().Service;

        var failure = await CaptureExceptionAsync(client.ReplaceClusterAsync(
            "orders",
            child => child.UseTransport(rejectedTransport),
            TimeSpan.Zero).AsTask());
        var retainedProxy = (OrdersProxy)client.CreateProxy<IOrdersContract>().Service;

        Ensure(failure is InvalidOperationException exception &&
               exception.Message.Contains("migration", StringComparison.OrdinalIgnoreCase),
            "candidate dynamic registration failure must abort replacement");
        Ensure(ReferenceEquals(oldProxy.Channel, retainedProxy.Channel),
            "dynamic migration rollback must preserve the old public route");
        Ensure(oldTransport.DisposeCount == 0 && rejectedTransport.DisposeCount == 1,
            "migration rollback must retain the old child and dispose only the candidate");
        Ensure(failure!.Message.Contains(MigrationCodecState.FailureMarker, StringComparison.Ordinal),
            "migration failure must retain the actual eager Codec factory failure marker");
        Ensure(migration.ScopeCreateCount == 1 && migration.FactoryCreateCount == 1 &&
               migration.ScopeDisposeCount == 1,
            "the real candidate must create one adapter scope, attempt its Codec factory once, and release that scope once");
    }

    // Prepare the existing controlled dynamic route under the coordinator's real owner.
    // No static-manifest label or production ownership check is bypassed.
    private static void AddPreparedUnregisterFixture(
        SharpLinkMultiClusterClient coordinator, SharpLinkClusterSlot slot,
        SharpLinkClusterRouteRegistration route)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domain = (ColdMetadataDomain)typeof(SharpLinkMultiClusterClient)
            .GetField("_metadataDomain", flags)!.GetValue(coordinator)!;
        var gate = (Lock)typeof(SharpLinkMultiClusterClient)
            .GetField("_gate", flags)!.GetValue(coordinator)!;
        var snapshotField = typeof(SharpLinkMultiClusterClient).GetField("_snapshot", flags)!;
        using var preparation = domain.BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(Manifest.Instance, preparation);
        SharpLinkGeneratedManifestStructureValidator.ValidatePrepared(manifest);
        using var previous = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
            .GetMethod("AcquireSnapshot", flags)!.Invoke(coordinator, null)!;
        preparation.ReserveCollection(1); // Mutable route map.
        preparation.ReserveCollection(1); // Frozen route map.
        var routes = new Dictionary<Type, SharpLinkClusterRouteRegistration>
        {
            [route.ContractType] = route
        }.ToFrozenDictionary();
        MultiClusterSnapshot? unownedSnapshot = null;
        DynamicAssemblyRegistration? unownedRegistration = null;
        try
        {
            unownedSnapshot = MultiClusterSnapshot.Create(previous.Clusters, routes,
                previous.ConfiguredConnectionBudget, preparation, previous, manifest);
            unownedRegistration = new DynamicAssemblyRegistration(slot, manifest.OwnerAssembly, manifest);
            lock (gate)
            {
                Ensure(ReferenceEquals(previous, snapshotField.GetValue(coordinator)),
                    "fixture publication must use the exact retained coordinator snapshot");
                var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                    .GetField("_dynamicRegistrations", flags)!.GetValue(coordinator)!;
                var capacity = checked(registrations.Count + 1);
                preparation.ReserveCollection(capacity);
                registrations.EnsureCapacity(capacity);
                registrations.Add(unownedRegistration);
                unownedRegistration = null; // The coordinator catalog now owns its exact metadata lease.
                try
                {
                    typeof(SharpLinkMultiClusterClient).GetMethod("PublishSnapshotLocked", flags)!
                        .Invoke(coordinator, [unownedSnapshot]);
                }
                finally
                {
                    // Publication can precede old-snapshot cleanup; do not dispose a transferred owner.
                    if (ReferenceEquals(unownedSnapshot, snapshotField.GetValue(coordinator)))
                        unownedSnapshot = null;
                }
            }
            preparation.Complete();
        }
        catch (Exception primary)
        {
            var failures = new List<Exception> { primary };
            try { unownedRegistration?.Dispose(); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            try { unownedSnapshot?.Dispose(); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
    }

    // This is a metadata-only coordinator catalog fixture, as in the original test.
    // The replacement child performs the real owned Codec preparation that must fail.
    private static void AddPreparedMigrationFixture(
        SharpLinkMultiClusterClient coordinator, MigrationCodecState state)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domain = (ColdMetadataDomain)typeof(SharpLinkMultiClusterClient)
            .GetField("_metadataDomain", flags)!.GetValue(coordinator)!;
        var gate = (Lock)typeof(SharpLinkMultiClusterClient)
            .GetField("_gate", flags)!.GetValue(coordinator)!;
        using var preparation = domain.BeginPreparation();
        var manifest = PreparedGeneratedManifest.Capture(new MigrationCodecManifest(state), preparation);
        SharpLinkGeneratedManifestStructureValidator.ValidatePrepared(manifest);
        using var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
            .GetMethod("AcquireSnapshot", flags)!.Invoke(coordinator, null)!;
        var registration = new DynamicAssemblyRegistration(
            snapshot.Clusters["orders"], manifest.OwnerAssembly, manifest);
        try
        {
            lock (gate)
            {
                Ensure(ReferenceEquals(snapshot, typeof(SharpLinkMultiClusterClient)
                    .GetField("_snapshot", flags)!.GetValue(coordinator)),
                    "fixture insertion must still target the exact owned coordinator snapshot");
                var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                    .GetField("_dynamicRegistrations", flags)!.GetValue(coordinator)!;
                registrations.Add(registration);
            }
        }
        catch (Exception primary)
        {
            try { registration.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
        // The catalog entry now owns its Retain lease; preparation admission and the
        // temporary snapshot are released before replacement or coordinator shutdown.
        preparation.Complete();
    }

    private sealed class MigrationCodecState
    {
        internal const string FailureMarker = "controlled eager migration Codec factory failure";
        internal int ScopeCreateCount;
        internal int FactoryCreateCount;
        internal int ScopeDisposeCount;
        internal MigrationCodecScope? Scope;
        internal readonly InvalidOperationException Failure = new(FailureMarker);
    }

    private sealed class MigrationCodecManifest : ISharpLinkGeneratedAssemblyManifest
    {
        private readonly IRpcGeneratedCodecFactory[] _codecs;
        internal MigrationCodecManifest(MigrationCodecState state)
            => _codecs = [new MigrationCodecFactory(state, new MigrationCodecAdapter(state))];
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "migration-codec-fixture";
        public Assembly OwnerAssembly => typeof(RollbackMarker).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x6d69677261746531UL, 0x636f6465632d7631UL);
        public string CompileTimeDescriptor => "owned-codec-only-migration-failure/v1";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => _codecs;
        public IReadOnlyList<string> Dependencies { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } = [];
        public IReadOnlyList<string> ContractDependencies { get; } = [];
    }

    private sealed class MigrationCodecFactory(MigrationCodecState state, MigrationCodecAdapter adapter)
        : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(RollbackMarker);
        public RpcHash128 CodecHash => new(0x6d69677261746532UL, 0x636f6465632d7631UL);
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            ArgumentNullException.ThrowIfNull(provider);
            Ensure(adapterScope is MigrationCodecScope && ReferenceEquals(adapterScope, state.Scope),
                "the actual candidate provider must pass this manifest's created adapter scope");
            Interlocked.Increment(ref state.FactoryCreateCount);
            throw state.Failure;
        }
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<RollbackMarker>;
    }

    private sealed class MigrationCodecAdapter(MigrationCodecState state) : IRpcCodecAdapter
    {
        public string AdapterId => "owned-migration-failure/v1";
        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref state.ScopeCreateCount);
            return state.Scope = new MigrationCodecScope(state);
        }
    }

    private sealed class MigrationCodecScope(MigrationCodecState state) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<
            [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
                System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>()
            => throw new InvalidOperationException("The selected factory fails before requesting a scope Codec.");
        public void Dispose() => Interlocked.Increment(ref state.ScopeDisposeCount);
    }

}
