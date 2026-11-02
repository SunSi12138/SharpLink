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
        var owner = new MutationFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize(useManifestGate: false);
            var oldFactory = owner.OldFactory = new OwnedMutationFactory();
            var replacementFactory = owner.ReplacementFactory = new OwnedMutationFactory();
            var oldTransport = oldFactory.Factory;
            var replacementTransport = replacementFactory.Factory;
            var client = owner.Client = CreateStaticBuilder()
                .AddCluster("orders", child => child.UseTransport(oldFactory.OfferToBuilder()),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            var dynamicAssembly = typeof(RollbackMarker).Assembly;
            var registration = client.RegisterAssembly("orders", dynamicAssembly);
            Ensure(registration.Succeeded, $"dynamic setup registration must succeed: {registration.Error}");
            var oldProxy = (OrdersProxy)client.CreateProxy<IOrdersContract>().Service;

            var replacement = client.ReplaceClusterAsync(
                "orders",
                child => child.UseTransport(replacementFactory.OfferToBuilder()),
                TimeSpan.FromSeconds(2)).AsTask();
            owner.Replacement = new MutationSource(replacement);
            await replacement;
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
        catch (Exception failure) { owner.Failures.Add(failure); }
        finally { await owner.FinishAsync(); }
        owner.ThrowIfFailed();
    }

    [Test]
    // The rollback plugin exposes process-wide construction gates and environment switches.
    [NotInParallel("rollback-plugin")]
    public async Task DynamicRegistrationShouldRejectASlotChangedWhileItsManifestLoads(CancellationToken testCancellationToken)
    {
        var owner = new MutationFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize(useManifestGate: true);
            var manifestStarted = owner.ManifestStarted;
            var manifestRelease = owner.ManifestRelease;
            SharpLinkClusterKey cluster = "plugins";
            var child = owner.Child = new BlockingRetiredClient();
            // Exact fixture Stop source, captured without calling Stop or changing its counters.
            var stopSignal = (TaskCompletionSource)(typeof(BlockingRetiredClient)
                .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(child) ?? throw new InvalidOperationException("missing original child Stop source"));
            owner.ChildStop = new MutationSource(stopSignal.Task);
            var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
            var client = owner.Client = new SharpLinkMultiClusterClient(
                new SharpLinkMultiClusterOptions(),
                new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                    .ToFrozenDictionary(),
                FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
                []);
            var registration = LongRunningTestWorker.Run(() =>
                client.RegisterAssembly(cluster, typeof(RollbackMarker).Assembly));
            owner.Registration = new MutationSource(registration);
            var startedWaiter = manifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            owner.ManifestWaiter = new MutationSource(startedWaiter);
            await startedWaiter;

            var removalSource = client.RemoveClusterAsync(cluster, TimeSpan.Zero).AsTask();
            owner.Removal = new MutationSource(removalSource);
            var removal = await removalSource;
            Ensure(removal is { Succeeded: true, ForcedStop: true },
                "the concurrent remove must publish while manifest loading is paused");
            manifestRelease.TrySetResult();

            var registrationWaiter = registration.WaitAsync(TimeSpan.FromSeconds(2));
            owner.RegistrationWaiter = new MutationSource(registrationWaiter);
            var result = await registrationWaiter;
            var registrations = (List<DynamicAssemblyRegistration>)typeof(SharpLinkMultiClusterClient)
                .GetField("_dynamicRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!;
            Ensure(!result.Succeeded &&
                   result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "registration must reject a slot that changed while its manifest loaded");
            Ensure(child.RegisterAssemblyCallCount == 0 && registrations.Count == 0,
                "registration must not reach or retain the retired child");

            child.ReleaseStop();
            await owner.StartStop();
        }
        catch (Exception failure) { owner.Failures.Add(failure); }
        finally { await owner.FinishAsync(); }
        owner.ThrowIfFailed();
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

    // Sources have fixed roles in the two controls above, not an extensible task registry.
    private sealed class MutationSource
    {
        internal readonly Task Source;
        internal readonly Task<Exception?> Outcome;
        internal MutationSource(Task source)
        {
            Source = source;
            Outcome = ReadOutcome(source); // Also consumes a late fault/cancellation while retained.
        }
        private static async Task<Exception?> ReadOutcome(Task source)
        {
            try { await source; return null; }
            catch (Exception failure) { return source.Exception is { } all ? all : failure; }
        }
        internal async Task<bool> Join(List<Exception> failures)
        {
            try { await Source.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception failure) { failures.Add(failure); }
            var ended = Source.IsCompleted;
            if (!ended) return false;
            try { await Outcome.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception failure) { failures.Add(failure); }
            if (!Outcome.IsCompletedSuccessfully) return false;
            if (await Outcome is { } outcome) failures.Add(outcome);
            return true;
        }
    }

    private sealed class OwnedMutationFactory : IClientTransportFactory
    {
        private readonly object _gate = new();
        internal readonly ControlledMutationTransportFactory Factory = new();
        private int _offered;
        private readonly List<MutationSource> _disposals = [];
        private readonly List<Exception> _synchronousDisposalFailures = [];
        internal bool Offered => Volatile.Read(ref _offered) != 0;
        internal (MutationSource[] Sources, Exception[] Failures) CaptureDisposals()
        {
            lock (_gate) return (_disposals.ToArray(), _synchronousDisposalFailures.ToArray());
        }
        internal OwnedMutationFactory OfferToBuilder()
        {
            Volatile.Write(ref _offered, 1); // Even a throwing UseTransport leaves transfer conservative.
            return this;
        }
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => Factory.ConnectAsync(cancellationToken);
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                // Do not cache away a duplicate invocation: the original inner
                // DisposeCount oracle must still see every real production call.
                try
                {
                    var original = Factory.DisposeAsync().AsTask();
                    _disposals.Add(new MutationSource(original));
                    return new ValueTask(original);
                }
                catch (Exception failure) { _synchronousDisposalFailures.Add(failure); throw; }
            }
        }
    }

    private sealed class MutationFixture
    {
        private static readonly List<MutationFixture> Retained = [];
        internal readonly List<Exception> Failures = [];
        internal readonly TaskCompletionSource ManifestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ManifestRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ISharpLinkMultiClusterClient? Client;
        internal OwnedMutationFactory? OldFactory;
        internal OwnedMutationFactory? ReplacementFactory;
        internal BlockingRetiredClient? Child;
        internal MutationSource? Replacement;
        internal MutationSource? Registration;
        internal MutationSource? Removal;
        internal MutationSource? ManifestWaiter;
        internal MutationSource? RegistrationWaiter;
        internal MutationSource? ChildStop;
        private MutationSource? _stop;
        private MutationSource? _dispose;
        private MutationSource? _untransferredChildDispose;
        private bool _stopInvoked;
        private string? _previousCodec;
        private bool _codecSnapshot;
        private bool _codecMutation;
        private TaskCompletionSource? _previousStarted;
        private TaskCompletionSource? _previousRelease;
        private bool _gateMutation;

        internal void Initialize(bool useManifestGate)
        {
            _previousCodec = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC");
            _codecSnapshot = true;
            _codecMutation = true;
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
            if (!useManifestGate) return;
            _previousStarted = RollbackState.ManifestConstructionStarted;
            _previousRelease = RollbackState.ManifestConstructionRelease;
            _gateMutation = true;
            RollbackState.ManifestConstructionStarted = ManifestStarted;
            RollbackState.ManifestConstructionRelease = ManifestRelease;
        }

        internal Task StartStop()
        {
            if (_stopInvoked)
                return _stop?.Source ?? throw new InvalidOperationException("original Stop invocation failed synchronously");
            _stopInvoked = true;
            _stop = new MutationSource(Client!.StopAsync().AsTask());
            return _stop.Source;
        }

        private async Task<bool> Join(MutationSource? source)
            => source is null || await source.Join(Failures);

        private async Task<bool> FinishFactory(OwnedMutationFactory? factory, bool operationsEnded)
        {
            if (factory is null) return true;
            // A pending replacement may still configure/publish this exact factory.
            // Never dispose it based only on a timeout or absence of a counter.
            if (!factory.Offered && operationsEnded)
            {
                try { _ = factory.DisposeAsync(); }
                catch (Exception failure) { Failures.Add(failure); }
            }
            var disposal = factory.CaptureDisposals();
            Failures.AddRange(disposal.Failures);
            if (disposal.Sources.Length == 0)
            {
                Failures.Add(new InvalidOperationException("factory disposal/transfer is not proved"));
                return false;
            }
            var ended = disposal.Failures.Length == 0;
            foreach (var original in disposal.Sources)
            {
                var joined = await original.Join(Failures);
                ended &= joined && original.Source.IsCompletedSuccessfully;
            }
            return ended;
        }

        internal async Task FinishAsync()
        {
            // Only this fixture's barriers: signalling is never mistaken for joining.
            ManifestRelease.TrySetResult();
            try { Child?.ReleaseStop(); } catch (Exception failure) { Failures.Add(failure); }
            try { OldFactory?.Factory.ReleaseConnect(); } catch (Exception failure) { Failures.Add(failure); }
            try { ReplacementFactory?.Factory.ReleaseConnect(); } catch (Exception failure) { Failures.Add(failure); }

            var operationsEnded = await Join(Replacement);
            operationsEnded &= await Join(Registration);
            operationsEnded &= await Join(Removal);
            operationsEnded &= await Join(ManifestWaiter);
            operationsEnded &= await Join(RegistrationWaiter);

            var coordinatorEnded = Client is null;
            if (Client is { } client)
            {
                if (!_stopInvoked)
                {
                    try { _ = StartStop(); } catch (Exception failure) { Failures.Add(failure); }
                }
                var stopJoined = await Join(_stop);
                try { _dispose = new MutationSource(client.DisposeAsync().AsTask()); }
                catch (Exception failure) { Failures.Add(failure); }
                var disposeJoined = await Join(_dispose);
                coordinatorEnded = stopJoined && disposeJoined &&
                    _stop?.Source.IsCompletedSuccessfully == true &&
                    _dispose?.Source.IsCompletedSuccessfully == true &&
                    client.LifecycleState == SharpLinkClientLifecycleState.Stopped;
            }
            else if (Child is { } child)
            {
                // The direct, synchronous coordinator constructor did not return;
                // it starts no workers and never invokes the child. Caller still owns it.
                try { _untransferredChildDispose = new MutationSource(child.DisposeAsync().AsTask()); }
                catch (Exception failure) { Failures.Add(failure); }
                coordinatorEnded = await Join(_untransferredChildDispose) &&
                    _untransferredChildDispose?.Source.IsCompletedSuccessfully == true;
            }

            var childEnded = Child is null;
            if (Child is { } ownedChild)
            {
                var childJoined = await Join(ChildStop);
                childEnded = childJoined &&
                    (ChildStop?.Source.IsCompletedSuccessfully == true ||
                     _untransferredChildDispose?.Source.IsCompletedSuccessfully == true) &&
                    ownedChild.StopStarted.Task.IsCompletedSuccessfully;
            }
            // Require actual successful disposal, not terminal fault or DisposeCount.
            var oldEnded = await FinishFactory(OldFactory, operationsEnded);
            var replacementEnded = await FinishFactory(ReplacementFactory, operationsEnded);
            var ended = operationsEnded && coordinatorEnded && childEnded && oldEnded && replacementEnded;
            if (!ended)
            {
                Failures.Add(new InvalidOperationException("mutation fixture retains unknown/unfinished owners and isolation"));
                lock (Retained) Retained.Add(this);
                return;
            }

            var restored = true;
            if (_gateMutation)
            {
                // Never overwrite a different owner's unexpected replacement gates.
                if (!ReferenceEquals(RollbackState.ManifestConstructionStarted, ManifestStarted) ||
                    !ReferenceEquals(RollbackState.ManifestConstructionRelease, ManifestRelease))
                {
                    Failures.Add(new InvalidOperationException("manifest gate ownership changed unexpectedly"));
                    lock (Retained) Retained.Add(this);
                    return;
                }
                else
                {
                    try { RollbackState.ManifestConstructionStarted = _previousStarted; }
                    catch (Exception failure) { Failures.Add(failure); restored = false; }
                    try { RollbackState.ManifestConstructionRelease = _previousRelease; }
                    catch (Exception failure) { Failures.Add(failure); restored = false; }
                }
            }
            if (_codecSnapshot && _codecMutation)
            {
                try { Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", _previousCodec); }
                catch (Exception failure) { Failures.Add(failure); restored = false; }
            }
            if (!restored)
            {
                lock (Retained) Retained.Add(this);
                return;
            }
            try { RollbackState.TestIsolation.Release(); }
            catch (Exception failure) { Failures.Add(failure); }
        }

        internal void ThrowIfFailed()
        {
            if (Failures.Count != 0) throw new AggregateException(Failures);
        }
    }

}
