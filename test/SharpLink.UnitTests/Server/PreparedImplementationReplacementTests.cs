using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed partial class PreparedImplementationReplacementTests
{
    [Test]
    public async Task MetadataPlanOwnsColdWriterButDoesNotSealOrPublish()
    {
        await using var fixture = await Fixture.Create();
        var old = fixture.OldModule;
        var services = fixture.Registry.ServicesStorage;
        await using (var plan = fixture.Prepare())
        {
            Ensure(fixture.Registry.IsColdWriterHeld, "writer lease acquired");
            Ensure(!plan.IsSealed && old.State == SharpLinkDynamicModuleState.Running, "metadata is not retirement");
            Ensure(ReferenceEquals(services, fixture.Registry.ServicesStorage), "metadata did not publish");
            Reject(() => fixture.Prepare(), "overlapping plan must reject");
            Ensure(!fixture.Server.RegisterAssembly(fixture.NewAssembly).Succeeded, "register writer must reject");
            var replace = await fixture.Server.ReplaceAssemblyAsync(fixture.OldAssembly, fixture.NewAssembly, TimeSpan.Zero);
            Ensure(!replace.Succeeded, "ordinary replacement writer must reject");
            Reject(() => fixture.Server.UnregisterAssemblyAsync(fixture.OldAssembly, TimeSpan.Zero), "unregister must reject");
        }
        Ensure(!fixture.Registry.IsColdWriterHeld, "pre-seal abort releases writer");
        Ensure(old.TryAcquire(false, out var lease), "old gate remains open before seal");
        lease.Dispose();
    }

    [Test]
    public async Task EqualWireHashCannotReplaceActualContractOwnerGeneration()
    {
        await using var fixture = await Fixture.Create();
        var context = new PluginContext();
        var contract = context.LoadFromAssemblyPath(Path.Combine(PluginPath, "SharpLink.DynamicPlugin.Contracts.dll"));
        var services = new PluginContext(contract);
        try
        {
            var replacement = services.LoadFromAssemblyPath(Path.Combine(PluginPath, "SharpLink.DynamicPlugin.Services.dll"));
            Reject(() => fixture.Server.PrepareImplementationReplacement([(fixture.OldAssembly, replacement)]),
                "same bytes/hash with distinct contract Type generation must reject");
            Ensure(!fixture.Registry.IsColdWriterHeld, "rejected owner releases writer");
            Ensure(fixture.OldModule.State == SharpLinkDynamicModuleState.Running, "old remains running");
        }
        finally
        {
            services.Unload();
            context.Unload();
        }
    }

    [Test]
    public async Task ContractOwnerAndDependencyClosureAreCheckedInsteadOfImplementationHash()
    {
        await using var fixture = await Fixture.Create();
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(fixture.NewAssembly, out var incoming).Succeeded, "incoming load");
        var changed = new ManifestView(incoming!) { OverrideDependencies = ["Missing.Generated.Dependency, Version=1.0.0.0"] };
        Reject(() => ServerImplementationReplacementValidation.Validate([fixture.OldModule], [changed], [fixture.ContractManifest]),
            "dependency mismatch must reject");
        fixture.ContractManifest.EmptyHash = true;
        try
        {
            Reject(() => fixture.Prepare(), "empty actual owning Contract hash must reject");
        }
        finally { fixture.ContractManifest.EmptyHash = false; }
        Ensure(!fixture.Registry.IsColdWriterHeld, "invalid metadata did not retain writer");
    }

    [Test]
    public async Task ChangedExactBaseRejectsBeforeRetirement()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        fixture.Registry.GenerationStorage++;
        await RejectAsync(() => ReleaseLocally(plan), "stale source revision must reject");
        Ensure(!plan.IsSealed, "stale base did not seal old");
        Ensure(fixture.OldModule.State == SharpLinkDynamicModuleState.Running, "old gate unchanged");
    }

    [Test]
    public async Task SameRevisionDifferentOldModuleObjectRejects()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        var old = fixture.OldModule;
        using var registration = fixture.Runtime.PrepareGeneratedManifest(old.Manifest);
        var impostor = new SharpLinkDynamicModule(fixture.OldAssembly, old.Manifest, registration);
        fixture.Registry.DynamicModules.Remove(fixture.OldAssembly);
        fixture.Registry.DynamicModules.Add(fixture.OldAssembly, impostor);
        try
        {
            await RejectAsync(() => ReleaseLocally(plan), "exact identity must reject even at same revision");
            Ensure(!plan.IsSealed && old.State == SharpLinkDynamicModuleState.Running, "old not retired");
        }
        finally
        {
            fixture.Registry.DynamicModules.Remove(fixture.OldAssembly);
            fixture.Registry.DynamicModules.Add(fixture.OldAssembly, old);
            impostor.TryBeginDraining();
            impostor.MarkReleased();
        }
    }

    [Test]
    public async Task NonemptyChangedOwningHashRejectsBeforeSeal()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        fixture.ContractManifest.OverrideHash = new RpcHash128(123, 456);
        try
        {
            await RejectAsync(() => ReleaseLocally(plan), "changed actual owner hash must not become a new base silently");
            Ensure(!plan.IsSealed, "changed owner rejected before seal");
        }
        finally { fixture.ContractManifest.OverrideHash = null; }
    }

    [Test]
    public async Task RuntimeMutationAfterMetadataPreparationRejectsBeforeSeal()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        fixture.Runtime.PublishGeneratedCodecs(fixture.Runtime.CreateGeneratedCodecSnapshot());
        await RejectAsync(() => ReleaseLocally(plan), "cold runtime revision must be part of prepared base");
        Ensure(!plan.IsSealed && fixture.OldModule.State == SharpLinkDynamicModuleState.Running,
            "stale runtime does not retire old gates");
    }

    [Test]
    public async Task OwningHashChangedInsideStubFactoryRejectsBeforeSeal()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        fixture.DuringStubPreparation = () => fixture.ContractManifest.OverrideHash = new RpcHash128(91, 92);
        var old = fixture.OldModule;
        await RejectAsync(() => ReleaseLocally(plan), "stub callbacks must not invalidate prepared owner hashes");
        Ensure(!plan.IsSealed && old.State == SharpLinkDynamicModuleState.Running,
            "callback metadata changes reject before irreversible retirement");
        Ensure(!fixture.Registry.IsColdWriterHeld, "unsealed rejection releases the writer");
    }

    [Test]
    public async Task CodecBindingPreparationFailureDisposesCandidateWithoutRetiringOld()
    {
        await using var fixture = await Fixture.Create();
        await using var plan = fixture.Prepare();
        var old = fixture.OldModule;
        fixture.FailStubPreparation = true;
        await RejectAsync(() => ReleaseLocally(plan), "injected codec preparation must fail");
        Ensure(!plan.IsSealed, "fallible preparation precedes permanent seal");
        Ensure(old.State == SharpLinkDynamicModuleState.Running, "old gate remains running");
        Ensure(!fixture.Registry.IsColdWriterHeld, "preparation failure releases writer");
        Ensure(!fixture.Registry.DynamicModules.ContainsKey(fixture.NewAssembly), "candidate never published");
    }

    [Test]
    public async Task RetiredConnectionScopeDisposalIsPhysicalCommitBarrier()
    {
        await using var fixture = await Fixture.Create();
        fixture.OldStatic("RetiredConnectionService", "Reset");
        var connection = await fixture.CreateConnectionService("IRetiredConnectionService");
        var connectionRegistry = Field<ServerConnectionRegistry>(fixture.Server, "_connectionRegistry");
        Ensure(connectionRegistry.TryRetire(connection), "connection retirement tracked");
        Ensure(connectionRegistry.TryRemove(new(connection.Session.Id, connection)), "retired-only owner");
        await using var plan = fixture.Prepare();
        var old = fixture.OldModule;
        var codecs = fixture.Runtime.CreateGeneratedCodecSnapshot();
        var release = ReleaseLocally(plan);
        await fixture.OldTask("RetiredConnectionService", "DisposeStarted").WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(plan.IsSealed && !release.IsCompleted, "physical disposal remains owned");
        Ensure(!old.TryAcquire(false, out _), "old gate permanently closed before disposal");
        Reject(() => CommitLocally(plan), "commit must not overtake async scope disposal");
        Ensure(!fixture.Registry.DynamicModules.ContainsKey(fixture.NewAssembly), "new implementation not published yet");
        fixture.OldStatic("RetiredConnectionService", "ReleaseDispose");
        await release.WaitAsync(TimeSpan.FromSeconds(5));
        CommitLocally(plan);
        Ensure(plan.IsCommitted && old.State == SharpLinkDynamicModuleState.Released, "commit follows actual old release");
        Ensure(ReferenceEquals(codecs, fixture.Runtime.CreateGeneratedCodecSnapshot()), "immutable global codec graph unchanged");
        Ensure(fixture.Registry.IsColdWriterHeld, "paused commit retains writer until coordinator completes");
        Ensure(fixture.Registry.DynamicModules.ContainsKey(fixture.NewAssembly), "new implementation published");
        Ensure(!old.TryAcquire(false, out _), "committed old gate never reopens");
        plan.Complete();
        Ensure(!fixture.Registry.IsColdWriterHeld, "completion releases cold writer");
        connectionRegistry.CompleteRetired(connection);
    }

    [Test]
    public async Task DisposalFailureRemainsClosedAndNeverPublishesCandidate()
    {
        await using var fixture = await Fixture.Create();
        fixture.OldStatic("FirstThrowingDisposalService", "Reset");
        fixture.CreateSingleton("IFirstThrowingDisposalService");
        fixture.OldStatic("FirstThrowingDisposalService", "EnableDisposeFailure");
        var old = fixture.OldModule;
        var plan = fixture.Prepare();
        await RejectAsync(() => ReleaseLocally(plan), "old disposal failure must propagate");
        Ensure(plan.IsSealed && !plan.IsCommitted, "failure classified after irreversible retirement");
        Reject(() => CommitLocally(plan), "failed physical release cannot commit");
        await plan.DisposeAsync();
        Ensure(fixture.Registry.IsColdWriterHeld, "failed-closed latch remains");
        Ensure(!old.TryAcquire(false, out _), "dispose does not reopen old");
        Ensure(!fixture.Server.RegisterAssembly(fixture.NewAssembly).Succeeded, "failed-closed prevents alternate ordinary writer");
        Ensure(!fixture.Registry.DynamicModules.ContainsKey(fixture.NewAssembly), "new implementation not published");
    }

    [Test]
    public async Task StopWinsDuringPhysicalDisposalWithoutCompetingForTheWriter()
    {
        await using var fixture = await Fixture.Create();
        fixture.OldStatic("RetiredConnectionService", "Reset");
        await fixture.CreateConnectionService("IRetiredConnectionService");
        await using var plan = fixture.Prepare();
        var release = ReleaseLocally(plan);
        await fixture.OldTask("RetiredConnectionService", "DisposeStarted").WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Server.StopAsync(TimeSpan.Zero).AsTask();
        Ensure(fixture.Server.LifecycleState != SharpLinkServerLifecycleState.Running, "stop wins lifecycle before waiting");
        Reject(() => CommitLocally(plan), "stop disallows commit during release");
        fixture.OldStatic("RetiredConnectionService", "ReleaseDispose");
        await release.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Reject(() => CommitLocally(plan), "stopped transaction cannot commit later");
        Ensure(!plan.IsCommitted, "stop did not publish candidate");
    }

    [Test]
    public async Task RuntimePreparedAdoptionRejectsStaleBaseWithoutChangingCodecs()
    {
        await using var fixture = await Fixture.Create();
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(fixture.NewAssembly, out var incoming).Succeeded, "incoming load");
        using var candidate = fixture.Runtime.PrepareGeneratedManifest(incoming!);
        var before = fixture.Runtime.CreateGeneratedCodecSnapshot();
        var plan = fixture.Runtime.PrepareImplementationManifests([fixture.OldModule.CodecRegistration], [candidate]);
        fixture.Runtime.PublishGeneratedCodecs(before);
        Reject(plan.Commit, "stale runtime base must reject");
        Ensure(ReferenceEquals(before, fixture.Runtime.CreateGeneratedCodecSnapshot()), "no generated registration graph changed");
    }

    [Test]
    public async Task CompletedRetainedPlanDoesNotRootOldCollectibleImplementation()
    {
        await using var fixture = await Fixture.Create();
        var (plan, oldContext) = await ReplaceAndUnloadOld(fixture);
        for (var attempt = 0; oldContext.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Ensure(!oldContext.IsAlive, "completed retained plan must release old ALC roots");
        Ensure(plan.IsCommitted, "retained receipt remains usable");
        GC.KeepAlive(plan);
    }

    [Test]
    public async Task FailedRetainedPlanDoesNotRootOldImplementationAfterStop()
    {
        await using var fixture = await Fixture.Create();
        var (plan, oldContext) = await FailStopAndUnloadOld(fixture);
        for (var attempt = 0; oldContext.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Ensure(!oldContext.IsAlive, "failed retained handle must not retain physical failure Task/old ALC");
        Ensure(plan.IsSealed && !plan.IsCommitted, "failed receipt remains closed");
        GC.KeepAlive(plan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(SharpLinkServer.PreparedImplementationReplacement Plan, WeakReference Context)>
        FailStopAndUnloadOld(Fixture fixture)
    {
        fixture.OldStatic("FirstThrowingDisposalService", "Reset");
        fixture.CreateSingleton("IFirstThrowingDisposalService");
        fixture.OldStatic("FirstThrowingDisposalService", "EnableDisposeFailure");
        var plan = fixture.Prepare();
        await RejectAsync(() => ReleaseLocally(plan), "injected disposal failure");
        await plan.DisposeAsync();
        await fixture.Server.StopAsync(TimeSpan.Zero);
        return (plan, fixture.UnloadOld());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(SharpLinkServer.PreparedImplementationReplacement Plan, WeakReference Context)>
        ReplaceAndUnloadOld(Fixture fixture)
    {
        fixture.CreateSingleton("IDynamicPluginService");
        var plan = fixture.Prepare();
        await ReleaseLocally(plan);
        CommitLocally(plan);
        plan.Complete();
        return (plan, fixture.UnloadOld());
    }
}
