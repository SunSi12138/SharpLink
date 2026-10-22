using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CooperativeMaintenanceOptInRejectsAdmissionConfigurationCallback(bool useTry, bool update)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseServiceProvider(services).UseTcp(0, IPAddress.Loopback).Build();
        if (update) server.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writer = Task.Factory.StartNew(() =>
        {
            void Configure(SharpLinkAdmissionControlOptions options)
            {
                entered.TrySetResult();
                release.Wait();
                options.Global.UseConcurrency(2);
            }
            if (useTry)
            {
                var result = update ? server.TryUpdateAdmissionControl(Configure) : server.TryEnableAdmissionControl(Configure);
                Ensure(result.Succeeded, "configuration admitted before opt-in must finish normally");
            }
            else if (update) server.UpdateAdmissionControl(Configure);
            else server.EnableAdmissionControl(Configure);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EnsureMaintenanceConfigurationRejected(server, "in-flight configure callback");
        }
        finally { release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        ConfigureMaintenanceTestDomain(server);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceOptInRejectsRejectedAdmissionCandidateDisposal(bool useTry)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseServiceProvider(services).UseTcp(0, IPAddress.Loopback).Build();
        server.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var kernel = ((SharpLinkServer)server).AdmissionStateKernelForTests!;
        var visited = false;
        kernel.BeforeReclaimedStateDisposalForTests = () =>
        {
            EnsureMaintenanceConfigurationRejected(server, "rejected candidate still owns physical disposal");
            visited = true;
        };
        try
        {
            if (useTry) Ensure(!server.TryEnableAdmissionControl(options => options.Global.UseConcurrency(2)).Succeeded,
                "duplicate enable remains rejected");
            else
            {
                var rejected = false;
                try { server.EnableAdmissionControl(options => options.Global.UseConcurrency(2)); }
                catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected, "duplicate enable remains rejected");
            }
            Ensure(visited, "physical candidate disposal callback was reached");
        }
        finally { kernel.BeforeReclaimedStateDisposalForTests = null; }
        ConfigureMaintenanceTestDomain(server);
    }

    [Test]
    public async Task CooperativeMaintenanceWaitsForEveryRetiredAdmissionGenerationDisposal()
    {
        AdmissionProgram? old = null;
        AdmissionStateKernel? kernel = null;
        await using var harness = await MaintenanceHarness.CreateAsync(beforeOptIn: server =>
        {
            ((ISharpLinkServer)server).EnableAdmissionControl(options => options.Global.UseConcurrency(1));
            old = server.CurrentAdmissionProgramForTests!;
            Ensure(old.TryAcquireUse(), "hold old policy generation through publication");
            try
            {
                ((ISharpLinkServer)server).UpdateAdmissionControl(options => options.Global.UseTokenBucket(rate =>
                { rate.TokenLimit = 32; rate.TokensPerPeriod = 32; rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1); }));
            }
            catch { old.ReleaseUse(); throw; }
            kernel = server.AdmissionStateKernelForTests!;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        kernel!.BeforeReclaimedStateDisposalForTests = () =>
        {
            Ensure(!Thread.CurrentThread.IsThreadPoolThread, "blocking disposal owns a dedicated worker");
            entered.TrySetResult();
            release.Wait();
        };
        var cleanup = Task.Factory.StartNew(old!.ReleaseUse, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(old.ActiveUses == 0 && !old.IsReclaimed && kernel.LiveProgramCount == 1,
                "retired physical disposal remains after old use/registration counts disappear");
            var operation = harness.Begin();
            await WaitForMaintenanceAsync(() => operation.Stage == SharpLinkMaintenanceStage.Draining);
            Ensure(!operation.WaitAsync().IsCompleted && harness.Plugin.GetStaticInt("Disposed") == 0,
                "retired policy physical disposal bars old implementation retirement");
            release.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"all old policy generations joined: {result.Failure}");
            await harness.VerifyIdentityAndShapesAsync();
        }
        finally { release.Set(); kernel.BeforeReclaimedStateDisposalForTests = null; await cleanup; }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceOptInRejectsInFlightServerCatalogPreparation(bool replacement)
    {
        using var plugin = PluginBundle.Load($"maintenance-server-prepare-{Guid.NewGuid()}", loadService: false);
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseServiceProvider(services).UseTcp(0, IPAddress.Loopback).Build();
        if (replacement) Ensure(server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "existing server contract");
        using var gated = new MaintenanceMetadataGateAssembly(plugin.ContractAssembly);
        var writer = Task.Factory.StartNew(() => replacement
            ? server.ReplaceAssemblyAsync(plugin.ContractAssembly, gated, TimeSpan.Zero).AsTask().GetAwaiter().GetResult().Succeeded
            : server.RegisterAssembly(gated).Succeeded,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            EnsureMaintenanceConfigurationRejected(server, "in-flight server metadata/candidate writer");
        }
        finally { gated.Release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        Ensure(!await writer, "delegating assembly is not an exact owner and must fail normal validation");
        ConfigureMaintenanceTestDomain(server);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CooperativeMaintenanceOptInRejectsDetachedTimedOutCatalogCleanup(bool serverSide, bool throws)
    {
        using var plugin = PluginBundle.Load($"maintenance-detached-cleanup-{Guid.NewGuid()}", loadService: false);
        using var services = new ServiceCollection().BuildServiceProvider();
        object owner = serverSide
            ? SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseServiceProvider(services).UseTcp(0, IPAddress.Loopback).Build()
            : SharpClientBuilder.Create().DisableRequestTimeout().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTcp(IPAddress.Loopback.ToString(), 1).Build();
        var disposedFailure = false;
        try
        {
            Ensure((serverSide ? ((ISharpLinkServer)owner).RegisterAssembly(plugin.ContractAssembly)
                : ((ISharpLinkClient)owner).RegisterAssembly(plugin.ContractAssembly)).Succeeded, "register retirement owner");
            var module = ServerRegistryTestAccessor.DynamicModule(owner, plugin.ContractAssembly);
            var scope = new MaintenanceRetirementScope(() =>
            {
                Ensure(!ServerRegistryTestAccessor.DynamicModules(owner).Contains(plugin.ContractAssembly),
                    "physical cleanup now owns the already detached registry member");
                EnsureOptInRejected();
            }, throws);
            var scopesField = typeof(RpcGeneratedManifestRegistration).GetField("_scopes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var existingScopes = (IRpcCodecAdapterScope[])scopesField.GetValue(module.CodecRegistration)!;
            scopesField.SetValue(module.CodecRegistration, (IRpcCodecAdapterScope[])[.. existingScopes, scope]);
            Ensure(module.TryAcquire(stream: false, out var use), "hold physical module owner beyond public timeout");
            SharpLinkAssemblyUnregisterResult receipt;
            try
            {
                receipt = serverSide
                    ? await ((ISharpLinkServer)owner).UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero)
                    : await ((ISharpLinkClient)owner).UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
                Ensure(!receipt.ReferencesReleased, "public retirement observation completed before physical release");
            }
            finally { use.Dispose(); }
            await WaitForMaintenanceAsync(() => module.State == SharpLinkDynamicModuleState.Released);
            var registry = serverSide
                ? typeof(SharpLinkServer).GetField("_serviceModuleRegistry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!
                : GetClientAssemblyRegistry((ISharpLinkClient)owner);
            var count = registry.GetType().GetField("_preparationWriters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await WaitForMaintenanceAsync(() => (int)count.GetValue(registry)! == 0);
            Ensure(scope.Visited, "post-detach physical cleanup callback ran");
            if (throws) EnsureOptInRejected();
            else Configure();
            GC.KeepAlive(receipt);

            void Configure()
            {
                if (serverSide) ConfigureMaintenanceTestDomain((ISharpLinkServer)owner);
                else ((ISharpLinkClient)owner).EnableCooperativeMaintenance(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
            }
            void EnsureOptInRejected()
            {
                var rejected = false;
                try { Configure(); }
                catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected, "opt-in sees the cleanup lease or its sticky physical failure, never a registry-removal gap");
            }
        }
        finally
        {
            try { await ((IAsyncDisposable)owner).DisposeAsync(); }
            catch (Exception exception) when (throws && exception.ToString().Contains("maintenance scope disposal failure", StringComparison.Ordinal))
            { disposedFailure = true; }
        }
        if (throws) Ensure(disposedFailure, "shutdown also reports the physical cleanup failure");
    }

    private sealed class MaintenanceRetirementScope(Action duringDispose, bool throws) : IRpcCodecAdapterScope
    {
        internal bool Visited;
        public IRpcCodec<T> CreateCodec<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>() => throw new NotSupportedException();
        public void Dispose()
        {
            duringDispose();
            Visited = true;
            if (throws) throw new InvalidOperationException("maintenance scope disposal failure");
        }
    }

    private static ISharpLinkMaintenanceController ConfigureMaintenanceTestDomain(ISharpLinkServer server)
        => server.EnableCooperativeMaintenance(Guid.NewGuid(), [new(Guid.NewGuid(), Guid.NewGuid(), "maintenance-test")]);

    private static void EnsureMaintenanceConfigurationRejected(ISharpLinkServer server, string detail)
    {
        var rejected = false;
        try { ConfigureMaintenanceTestDomain(server); }
        catch (InvalidOperationException) { rejected = true; }
        Ensure(rejected, detail);
    }
}
