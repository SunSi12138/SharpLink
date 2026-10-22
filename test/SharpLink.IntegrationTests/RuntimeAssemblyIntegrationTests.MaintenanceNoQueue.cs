namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [Arguments("builder", false)]
    [Arguments("builder", true)]
    [Arguments("enable", false)]
    [Arguments("enable", true)]
    [Arguments("update", false)]
    [Arguments("update", true)]
    public async Task CooperativeMaintenanceRejectsConfiguredWaitingWithoutChangingIt(string publication, bool oneWay)
    {
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback);
        if (publication == "builder") builder.UseAdmissionControl(Configure);
        await using var server = builder.Build();
        if (publication == "enable") server.EnableAdmissionControl(Configure);
        if (publication == "update")
        {
            server.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
            server.UpdateAdmissionControl(Configure);
        }
        var current = ((SharpLinkServer)server).CurrentAdmissionProgramForTests!;
        Ensure(current.Controller.QueuedCalls == 0, "empty queue does not imply maintenance eligibility");
        EnsureMaintenanceWaitingRejected(server);
        Ensure(ReferenceEquals(current, ((SharpLinkServer)server).CurrentAdmissionProgramForTests) &&
            !current.IsRetired && current.Controller.MaxQueuedCallsForTests == 8 &&
            current.Controller.MaxQueuedBytesForTests == 8192 && current.Controller.QueueOneWayCalls == oneWay,
            "rejected opt-in must not alter ordinary queue configuration or publication");
        server.DisableAdmissionControl();
        ConfigureMaintenanceTestDomain(server);

        void Configure(SharpLinkAdmissionControlOptions options)
        {
            ConfigureMaintenanceWaiting(options);
            options.QueueOneWayCalls = oneWay;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceRejectsRetainedAndReclaimingWaitingGeneration(bool disable)
    {
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback).UseAdmissionControl(ConfigureMaintenanceWaiting).Build();
        var implementation = (SharpLinkServer)server;
        var old = implementation.CurrentAdmissionProgramForTests!;
        Ensure(old.TryAcquireUse(), "hold old waiting-capable generation");
        try
        {
            if (disable) server.DisableAdmissionControl();
            else server.UpdateAdmissionControl(options => options.Global.UseTokenBucket(rate =>
            { rate.TokenLimit = 32; rate.TokensPerPeriod = 32; rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1); }));
            EnsureMaintenanceWaitingRejected(server);
        }
        catch { old.ReleaseUse(); throw; }
        var kernel = implementation.AdmissionStateKernelForTests!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        kernel.BeforeReclaimedStateDisposalForTests = () =>
        {
            Ensure(!Thread.CurrentThread.IsThreadPoolThread, "physical disposal gate is on a dedicated worker");
            entered.TrySetResult();
            release.Wait();
        };
        var cleanup = Task.Factory.StartNew(old.ReleaseUse, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(old.ActiveUses == 0 && !old.IsReclaimed, "removed old generation still owns physical disposal");
            EnsureMaintenanceWaitingRejected(server);
        }
        finally
        {
            release.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
            kernel.BeforeReclaimedStateDisposalForTests = null;
        }
        Ensure(old.IsReclaimed, "physical cleanup completed");
        ConfigureMaintenanceTestDomain(server);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceRejectsReentrantWaitingCandidatePreparationAndDisposal(bool useTry)
    {
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback).UseAdmissionControl(options => options.Global.UseConcurrency(1)).Build();
        var kernel = ((SharpLinkServer)server).AdmissionStateKernelForTests!;
        var disposed = false;
        kernel.BeforeReclaimedStateDisposalForTests = () =>
        {
            EnsureMaintenanceConfigurationRejected(server, "waiting-capable losing candidate still owns cleanup");
            disposed = true;
        };
        try
        {
            if (useTry) Ensure(!server.TryEnableAdmissionControl(Configure).Succeeded, "duplicate enable is rejected");
            else
            {
                var rejected = false;
                try { server.EnableAdmissionControl(Configure); }
                catch (InvalidOperationException) { rejected = true; }
                Ensure(rejected, "duplicate enable is rejected");
            }
        }
        finally { kernel.BeforeReclaimedStateDisposalForTests = null; }
        Ensure(disposed, "unpublished candidate cleanup was exercised");
        ConfigureMaintenanceTestDomain(server);

        void Configure(SharpLinkAdmissionControlOptions options)
        {
            EnsureMaintenanceConfigurationRejected(server, "queue writer callback precedes candidate registration");
            ConfigureMaintenanceWaiting(options);
            options.Global.UseConcurrency(2);
        }
    }

    [Test]
    public async Task CooperativeMaintenanceCannotEnableWaitingOrChangeAdmissionAfterOptIn()
    {
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback).UseAdmissionControl(options => options.Global.UseConcurrency(1)).Build();
        var current = ((SharpLinkServer)server).CurrentAdmissionProgramForTests!;
        ConfigureMaintenanceTestDomain(server);
        var callback = false;
        void Configure(SharpLinkAdmissionControlOptions options) { callback = true; ConfigureMaintenanceWaiting(options); }
        Ensure(!server.TryEnableAdmissionControl(Configure).Succeeded && !server.TryUpdateAdmissionControl(Configure).Succeeded &&
            !server.TryDisableAdmissionControl().Succeeded, "Try admission writers remain sealed");
        foreach (var write in new Action[] { () => server.EnableAdmissionControl(Configure),
                     () => server.UpdateAdmissionControl(Configure), server.DisableAdmissionControl })
        {
            var rejected = false;
            try { write(); } catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected, "throwing admission writer remains sealed");
        }
        Ensure(!callback && ReferenceEquals(current, ((SharpLinkServer)server).CurrentAdmissionProgramForTests),
            "sealed writers neither run callbacks nor mutate the published immediate-admission program");
    }

    private static void ConfigureMaintenanceWaiting(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(1);
        options.MaxQueuedCalls = 8;
        options.MaxQueuedBytes = 8192;
        options.MaxQueueDelay = TimeSpan.FromSeconds(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CooperativeMaintenanceRetainsUnpublishedAdmissionCleanupFailure(bool throws)
    {
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(0, IPAddress.Loopback).Build();
        var kernel = ((SharpLinkServer)server).AdmissionStateKernelForTests!;
        var invalid = false;
        try { server.EnableAdmissionControl(options => options.MaxQueuedCalls = -1); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Ensure(invalid, "ordinary validation error was observed");
        kernel.ValidateMaintenanceEligibility();
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseFixedWindow(rule => { rule.PermitLimit = 1; rule.Window = TimeSpan.FromSeconds(1); });
        var candidate = SharpLinkAdmissionController.Create(kernel, options, [], TimeProvider.System, ownsKernel: false);
        Ensure(kernel.LiveProgramCount == 0, "candidate has not registered any admission program");
        var counter = typeof(AdmissionRateState).GetField("_fixedCounter",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(candidate.GlobalRateStateForTests)!;
        var timer = new MaintenanceCleanupTimer(throws);
        counter.GetType().GetField("_timer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(counter, timer);
        InvalidOperationException? failure = null;
        try { kernel.ReleaseUnpublishedBindings(candidate); }
        catch (InvalidOperationException exception) { failure = exception; }
        Ensure(timer.Disposed && (failure is not null) == throws, "actual unpublished binding disposer was exercised");
        if (throws)
        {
            EnsureMaintenanceConfigurationRejected(server, "failed physical cleanup has no registered program owner");
            Ensure(!kernel.IsMaintenanceQuiescent, "failed unpublished cleanup cannot certify physical quiescence");
        }
        else
        {
            Ensure(kernel.IsMaintenanceQuiescent, "successful unpublished cleanup remains eligible");
            ConfigureMaintenanceTestDomain(server);
        }
    }

    private sealed class MaintenanceCleanupTimer(bool throws) : ITimer
    {
        internal bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose()
        {
            Disposed = true;
            if (throws) throw new InvalidOperationException("controlled unpublished timer cleanup failure");
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private static void EnsureMaintenanceWaitingRejected(ISharpLinkServer server)
    {
        InvalidOperationException? failure = null;
        try { ConfigureMaintenanceTestDomain(server); }
        catch (InvalidOperationException exception) { failure = exception; }
        Ensure(failure?.Message.Contains("Queued admission", StringComparison.Ordinal) == true,
            $"unsupported waiting configuration must fail explicitly at cold opt-in: {failure}");
    }
}
