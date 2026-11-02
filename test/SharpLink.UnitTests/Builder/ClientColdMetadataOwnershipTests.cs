using SharpLink.Sdk;
using System.Collections;
using System.Collections.Frozen;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Builder;

// Source controls for the staging owner graph; separate from the behavioral caller census.
public sealed partial class BuildPlanBuilderTests
{
    [Test]
    public void ClientDiscardMustReleasePendingMetadataAfterUnbuiltTransportCleanup()
    {
        var authority = new ColdMetadataAuthority();
        var domain = authority.CreateDomain();
        var transport = new TrackingClientTransport();
        var builder = CreateClientBuilder().UseTransport(transport);
        var plan = builder.CompileForMultiCluster([], domain);
        Ensure(authority.Bytes != 0, "a pending Client plan must own its admitted metadata header");

        builder.DiscardCompiledPlan(plan);

        Ensure(authority.Bytes == 0, "discard must release the pending Runtime metadata owner");
        Ensure(transport.DisposeCount == 1, "discard must dispose the unbuilt transport exactly once");
        Ensure(Capture(() => builder.MaterializeCompiledPlan(plan)) is InvalidOperationException,
            "discarded Client metadata cannot be materialized");
    }

    [Test]
    public async Task ClientMaterializationMustTransferExactAuthorityAndClearPendingPlanRoot()
    {
        var authority = new ColdMetadataAuthority();
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());
        var plan = builder.CompileForMultiCluster([], authority.CreateDomain());
        var client = builder.MaterializeCompiledPlan(plan);
        try
        {
            Ensure(ReferenceEquals(((SharpLinkRuntimeContext)client.RuntimeContext).Authority, authority),
                "materialization must transfer the exact metadata authority, not compile again");
            Ensure(Capture(() => _ = plan.RuntimeContext) is InvalidOperationException,
                "the consumed Client plan must not retain an accessible Runtime metadata root");
            Ensure(authority.Bytes != 0, "the live Runtime must retain its metadata owner");
        }
        finally { await client.DisposeAsync(); }
        Ensure(authority.Bytes == 0, "Client physical cleanup must release transferred metadata");
    }

    [Test]
    public void ClientTopologyFailureAfterRuntimeCompileMustReleaseMetadata()
    {
        var authority = new ColdMetadataAuthority();
        var builder = CreateClientBuilder().UseEndpoints([], static _ => new TrackingClientTransport());
        Ensure(Capture(() => builder.CompileForMultiCluster([], authority.CreateDomain())) is not null,
            "an empty static topology must fail after Runtime compilation");
        Ensure(authority.Bytes == 0, "post-Compile topology failure must not strand Runtime preparation");
    }

    [Test]
    public void FixedRouteSourceMustNotTouchInputUntilReservedCapture()
    {
        var authority = new ColdMetadataAuthority();
        var input = new ThrowingRouteManifestList(authority);
        var source = new FixedGeneratedClusterRouteSource(input);
        Ensure(input.CountCalls == 0, "Fixed route source construction must not eagerly enumerate or allocate a copy");
        using (var preparation = authority.CreateDomain().BeginPreparation())
        {
            Ensure(Capture(() => GeneratedClusterRouteSnapshot.Capture(source, preparation)) is InvalidOperationException,
                "the controlled input Count failure must propagate from bounded capture");
            Ensure(input.CountCalls == 1 && input.SawReservedHeader,
                "the first metadata callback must observe an admitted header reservation");
        }
        Ensure(authority.Bytes == 0, "failed route capture must release its reservation bundle");
    }

    [Test]
    public async Task CoordinatorStopMustJoinAnAdmittedRuntimeRouteCaptureBeforeChildCleanup()
    {
        var initialTransport = new TrackingClientTransport();
        var incomingTransport = new TrackingClientTransport();
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .UseGeneratedDiscoverySources(FixedGeneratedManifestSource.Empty, FixedGeneratedClusterRouteSource.Empty)
            .AddCluster("initial", child => child.UseTransport(initialTransport),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        using var source = new BlockingRouteSource();
        var control = (ISharpLinkMultiClusterLifecycleControl)client;
        var add = LongRunningTestWorker.Run(() => control.AddClusterAsync(
            "incoming", SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(incomingTransport),
            true, CancellationToken.None, FixedGeneratedManifestSource.Empty, source).AsTask().GetAwaiter().GetResult());
        Task? stop = null;
        try
        {
            Ensure(source.Entered.Wait(TimeSpan.FromSeconds(5)), "runtime Add must reach the real source callback barrier");
            stop = client.StopAsync().AsTask();
            Ensure(!stop.IsCompleted, "root Stop must join the admitted callback, not tear down child metadata underneath it");
            Ensure(initialTransport.DisposeCount == 0, "child cleanup must wait until admitted coordinator preparation leaves");
        }
        finally { source.Release.Set(); }
        try { await add.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException) { /* sealed child admission rejects the in-flight unpublished candidate */ }
        await (stop ?? client.StopAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(source.Calls == 1, "shutdown must not replay the route provider callback");
        Ensure(initialTransport.DisposeCount == 1 && incomingTransport.DisposeCount == 1,
            "the published child and failed candidate transport must both reach their actual cleanup owners");
    }

    [Test]
    public async Task CoordinatorStatusReaderMustRetainSnapshotUntilBlockedChildGetterReturns()
    {
        var authority = new ColdMetadataAuthority();
        var root = authority.CreateDomain();
        using var preparation = root.BeginPreparation();
        var builder = CreateClientBuilder().UseTransport(new TrackingClientTransport());
        var child = builder.MaterializeCompiledPlan(builder.CompileForMultiCluster([], authority.CreateDomain(root)));
        using var barrier = new StatusBarrierClient(child);
        preparation.ReserveCollection(1);
        var slots = new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot>
        { ["owned"] = new("owned", barrier, true) }.ToFrozenDictionary();
        await using var coordinator = new SharpLinkMultiClusterClient(new SharpLinkMultiClusterOptions(), slots,
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty, [],
            metadataDomain: root, metadataOwner: preparation.Retain(), metadataPreparation: preparation);
        preparation.Complete();
        preparation.Dispose();
        var query = LongRunningTestWorker.Run(() => coordinator.TryGetClusterStatus("owned", out _));
        try
        {
            Ensure(barrier.Entered.Wait(TimeSpan.FromSeconds(5)), "actual Status must enter its controlled child getter");
            await coordinator.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(authority.Bytes != 0,
                "publication/Stop must not release the old snapshot charge while an actual Status reader owns it");
        }
        finally { barrier.Release.Set(); }
        Ensure(await query.WaitAsync(TimeSpan.FromSeconds(5)), "the captured Status lookup must finish on its original child");
        Ensure(authority.Bytes == 0, "the final cold reader must release old snapshot/source charges without GC cleanup");
    }

    private sealed class StatusBarrierClient(ISharpLinkClient inner) : ISharpLinkClient, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Release = new(false);
        public SharpLinkConnectionState State
        { get { Entered.Set(); Release.Wait(); return inner.State; } }
        public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;
        public SharpLinkClientLifecycleState LifecycleState => inner.LifecycleState;
        public SharpLinkReadinessState Readiness => inner.Readiness;
        public SharpLinkClusterState ClusterState => inner.ClusterState;
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default) => inner.WaitForReadyAsync(cancellationToken);
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => inner.WaitForShutdownAsync(cancellationToken);

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => inner.CheckHealthAsync(cancellationToken);
        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly) => inner.RegisterAssembly(assembly);
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(Assembly assembly,
            TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.UnregisterAssemblyAsync(assembly, timeout, cancellationToken);
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(Assembly oldAssembly,
            Assembly newAssembly, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.ReplaceAssemblyAsync(oldAssembly, newAssembly, timeout, cancellationToken);
        public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
            => inner.CreateProxy<TContract>(options);
        public RpcLocalContract GetLocalContract<TContract>() where TContract : IService => inner.GetLocalContract<TContract>();
        public RpcLocalContract GetLocalContract(Assembly assembly, long id) => inner.GetLocalContract(assembly, id);
        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => inner.CreateContractChannel(local, options);
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class BlockingRouteSource : IGeneratedClusterRouteSource, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Calls;
        public IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot()
        {
            Calls++;
            Entered.Set();
            Release.Wait();
            return [];
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class ThrowingRouteManifestList(ColdMetadataAuthority authority)
        : IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest>
    {
        internal int CountCalls;
        internal bool SawReservedHeader;
        public int Count
        {
            get
            {
                CountCalls++;
                SawReservedHeader = authority.Bytes >= ColdMetadataAuthority.HeaderCharge;
                throw new InvalidOperationException("controlled route Count failure");
            }
        }
        public ISharpLinkGeneratedClusterRouteManifest this[int index] => throw new InvalidOperationException();
        public IEnumerator<ISharpLinkGeneratedClusterRouteManifest> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
