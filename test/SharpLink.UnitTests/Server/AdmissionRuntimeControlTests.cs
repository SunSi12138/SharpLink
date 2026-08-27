using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionRuntimeControlTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> TenantSelector =
        static _ => "tenant-a";

    [Test]
    public void UnsupportedServerShouldRejectRuntimeAdmissionControl()
    {
        ISharpLinkServer server = new UnsupportedServer();

        Ensure(CaptureFailure(() => server.EnableAdmissionControl(
            options => options.Global.UseConcurrency(1))) is NotSupportedException,
            "unsupported server must reject public enable");
        Ensure(CaptureFailure(server.DisableAdmissionControl) is NotSupportedException,
            "unsupported server must reject public disable");
    }

    [Test]
    [NotInParallel]
    public async Task PublicEnableFailuresShouldBeTransactionalAndEnabledUpdateShouldBeRejected()
    {
        await using var server = CreateServer();
        var kernel = server.AdmissionStateKernelForTests!;

        var callbackFailure = CaptureFailure(() =>
            ((ISharpLinkServer)server).EnableAdmissionControl(
                _ => throw new TestConfigurationException()));
        Ensure(callbackFailure is TestConfigurationException,
            "configuration callback failure must escape unchanged");
        AssertDisabledAndEmpty(server, kernel, "callback failure");

        var validationFailure = CaptureFailure(() =>
            ((ISharpLinkServer)server).EnableAdmissionControl(_ => { }));
        Ensure(validationFailure is InvalidOperationException,
            "invalid empty policy must fail validation before publication");
        AssertDisabledAndEmpty(server, kernel, "validation failure");

        var resolutionFailure = CaptureFailure(() =>
            ((ISharpLinkServer)server).EnableAdmissionControl(options =>
                options.AddContract<IMissingAdmissionContract>(
                    rule => rule.UseConcurrency(1))));
        Ensure(resolutionFailure is InvalidOperationException,
            "missing generated contract must fail candidate resolution");
        AssertDisabledAndEmpty(server, kernel, "resolution failure");

        SharpLinkConcurrencyLimitOptions? leaked = null;
        ((ISharpLinkServer)server).EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            leaked = options.Global.Concurrency;
        });
        var published = server.CurrentAdmissionProgramForTests!;
        leaked!.PermitLimit = 2;

        var held = await published.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        var blocked = await published.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired && !blocked.IsAcquired && blocked.Reason == "concurrency",
            "post-return option mutation must not alter the published program");
        held.Lease!.Dispose();

        var enabledUpdateFailure = CaptureFailure(() =>
            ((ISharpLinkServer)server).EnableAdmissionControl(
                options => options.Global.UseConcurrency(2)));
        Ensure(enabledUpdateFailure is InvalidOperationException,
            "enabled-to-enabled policy update must be rejected");
        Ensure(ReferenceEquals(server.CurrentAdmissionProgramForTests, published),
            "rejected enabled update must leave the current publication unchanged");
        Ensure(kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
               kernel.RuleStateCount == 1,
            "rejected candidate must reclaim without growing generation or state registries");

        ((ISharpLinkServer)server).DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "final disable");
    }

    [Test]
    [NotInParallel]
    public async Task PublicReEnableShouldReuseCompatibleConcurrencyRateAndPartitionStateDuringOverlap()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(ConfigureRateAndPartition);
        var original = server.CurrentAdmissionProgramForTests!;
        var kernel = original.Kernel;
        Ensure(original.TryAcquireUse(), "test must retain generation N across public disable");

        var first = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(first.IsAcquired, "generation N must consume shared permits/rate and create partition state");
        Ensure(kernel.RuleStateCount == 1 && kernel.PartitionStateCount == 1,
            "generation N must own one global rule state and one partition namespace");

        publicServer.DisableAdmissionControl();
        Ensure(original.IsRetired && !original.IsReclaimed && original.ActiveUses == 1,
            "public disable must retire N without invalidating a captured use");
        publicServer.EnableAdmissionControl(ConfigureRateAndPartition);
        var replacement = server.CurrentAdmissionProgramForTests!;

        Ensure(ReferenceEquals(
                original.Controller.GlobalStateForTests,
                replacement.Controller.GlobalStateForTests),
            "compatible public re-enable must reuse global limiter state");
        Ensure(ReferenceEquals(
                original.Controller.PartitionStateForTests,
                replacement.Controller.PartitionStateForTests),
            "compatible public re-enable must reuse the partition namespace");
        Ensure(kernel.LiveProgramCount == 2 && kernel.RetiredProgramCount == 1 &&
               kernel.RuleStateCount == 1 && kernel.PartitionStateCount == 1,
            "overlap must not duplicate compatible state registries");

        var blockedByOldPermit = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!blockedByOldPermit.IsAcquired && blockedByOldPermit.Reason == "concurrency",
            "old concurrency permit must constrain the compatible re-enabled generation");

        first.Lease!.Dispose();
        var exhausted = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "public re-enable must preserve already-consumed rate quota");

        original.ReleaseUse();
        Ensure(original.IsReclaimed && original.ReclaimCount == 1,
            "last old-generation use must reclaim exactly once");
        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "overlap cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task PublicReEnableShouldShareOldQueueAccountingDuringOverlap()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(ConfigureQueue);
        var original = server.CurrentAdmissionProgramForTests!;
        var kernel = original.Kernel;
        Ensure(original.TryAcquireUse() && original.TryAcquireUse(),
            "test must retain active and queued generation-N uses");
        var held = await original.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(held.IsAcquired, "generation N must hold the shared concurrency permit");
        var queued = original.Controller.AcquireAsync(
            CreateContext(), retainedBytes: 2, allowQueue: true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => kernel.QueuedCalls == 1,
            "generation N must reserve one shared queue slot");

        publicServer.DisableAdmissionControl();
        publicServer.EnableAdmissionControl(ConfigureQueue);
        var replacement = server.CurrentAdmissionProgramForTests!;
        Ensure(ReferenceEquals(
                original.Controller.GlobalStateForTests,
                replacement.Controller.GlobalStateForTests),
            "re-enabled queue policy must share compatible global state");
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 2 && kernel.RuleStateCount == 1,
            "old queued call and re-enabled generation must use one queue accounting kernel");

        var rejected = await replacement.Controller.AcquireAsync(
            CreateContext(), retainedBytes: 2, allowQueue: true, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == "queue_count",
            "re-enabled generation must observe old generation queue occupancy");
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 2,
            "rejected re-enabled enqueue must not underflow shared queue accounting");

        held.Lease!.Dispose();
        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired, "old queued call must survive disable/re-enable overlap");
        admitted.Lease!.Dispose();
        original.ReleaseUse();
        original.ReleaseUse();
        Ensure(original.IsReclaimed && original.ReclaimCount == 1,
            "old queued generation must reclaim exactly once after simulated captures release");
        await WaitUntilAsync(
            () => kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "shared queue and permit accounting must drain without underflow");
        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "queue overlap cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentPublicEnablesShouldPublishExactlyOneCandidate()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        var kernel = server.AdmissionStateKernelForTests!;
        using var bothBuilt = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, _) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                bothBuilt.Signal();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("concurrent enable release timed out");
            };

            var first = Task.Run(() => CaptureFailure(() =>
                publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1))));
            var second = Task.Run(() => CaptureFailure(() =>
                publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1))));
            Ensure(bothBuilt.Wait(TimeSpan.FromSeconds(5)),
                "both fully-built candidates must reach the pre-publication seam");
            release.Set();

            var failures = await Task.WhenAll(first, second);
            Ensure(failures.Count(failure => failure is null) == 1 &&
                   failures.Count(failure => failure is InvalidOperationException) == 1,
                "exactly one concurrent enable must win publication");
            Ensure(server.CurrentAdmissionProgramForTests is not null &&
                   kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
                   kernel.RuleStateCount == 1,
                "losing candidate must reclaim completely while the winner remains current");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            release.Set();
        }

        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "concurrent enable cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task EnableRacingDisableShouldLinearizeInWriterOrder()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        var kernel = server.AdmissionStateKernelForTests!;
        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var original = server.CurrentAdmissionProgramForTests!;
        using var candidateBuilt = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, _) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                candidateBuilt.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("enable-vs-disable release timed out");
            };

            var enable = Task.Run(() => CaptureFailure(() =>
                publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1))));
            Ensure(candidateBuilt.Wait(TimeSpan.FromSeconds(5)),
                "enable candidate must be fully built before the competing disable wins");
            publicServer.DisableAdmissionControl();
            Ensure(server.CurrentAdmissionProgramForTests is null,
                "disable must be visible before the blocked enable is released");
            release.Set();
            Ensure(await enable is null,
                "enable that linearizes after disable must succeed as a re-enable");
            Ensure(server.CurrentAdmissionProgramForTests is not null &&
                   original.IsRetired && original.IsReclaimed &&
                   kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
                   kernel.RuleStateCount == 1,
                "final state must match disable-then-enable publication order without registry growth");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            release.Set();
        }

        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "enable-vs-disable cleanup");
    }

    [Test]
    [NotInParallel]
    public async Task CandidateBuiltBeforeStopShouldBeRejectedAndReclaimed()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        var kernel = server.AdmissionStateKernelForTests!;
        AdmissionProgram? candidate = null;
        Task? stopTask = null;

        try
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, observed) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                candidate = observed;
                stopTask = owner.StopAsync(TimeSpan.Zero).AsTask();
                Ensure(SpinWait.SpinUntil(() => kernel.IsDraining, TimeSpan.FromSeconds(5)),
                    "Stop must seal the admission control plane before candidate publication resumes");
            };

            var failure = CaptureFailure(() => publicServer.EnableAdmissionControl(
                options => options.Global.UseConcurrency(1)));
            Ensure(failure is InvalidOperationException,
                "candidate publication after Stop seal must be rejected");
            Ensure(candidate is not null, "candidate-built seam must observe the complete candidate");
            Ensure(stopTask is not null, "candidate-built seam must start Stop");
            await stopTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(candidate!.IsRetired && candidate.IsReclaimed && candidate.ReclaimCount == 1,
                "Stop-racing candidate must retire and reclaim exactly once");
            Ensure(CaptureFailure(publicServer.DisableAdmissionControl) is InvalidOperationException,
                "disable after lifecycle sealing must deterministically reject without publishing");
            AssertKernelDrained(kernel, "candidate-vs-Stop");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
        }
    }

    [Test]
    [NotInParallel]
    public async Task RepeatedEnableDisableCyclesShouldKeepRegistriesBounded()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        var kernel = server.AdmissionStateKernelForTests!;

        publicServer.DisableAdmissionControl();
        publicServer.DisableAdmissionControl();
        AssertDisabledAndEmpty(server, kernel, "repeated initial disable");

        for (var index = 0; index < 64; index++)
        {
            publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(2));
            Ensure(kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
                   kernel.RuleStateCount == 1,
                "each enabled cycle must have exactly one current generation and state entry");
            publicServer.DisableAdmissionControl();
            publicServer.DisableAdmissionControl();
            AssertDisabledAndEmpty(server, kernel, $"cycle {index}");
        }
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static void ConfigureRateAndPartition(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(1);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });
        options.UsePartition(TenantSelector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseConcurrency(1);
        });
    }

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(1);
        options.MaxQueuedCalls = 1;
        options.MaxQueuedBytes = 8;
        options.MaxQueueDelay = TimeSpan.FromSeconds(5);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "runtime-control-test", null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertDisabledAndEmpty(
        SharpLinkServer server,
        AdmissionStateKernel kernel,
        string scenario)
    {
        Ensure(server.CurrentAdmissionProgramForTests is null,
            $"{scenario}: publication must remain disabled");
        Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
               kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0 &&
               kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            $"{scenario}: candidate/state/accounting registries must be empty");
    }

    private static void AssertKernelDrained(AdmissionStateKernel kernel, string scenario)
        => Ensure(
            kernel.IsDraining && kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
            kernel.RuleStateCount == 0 && kernel.PartitionStateCount == 0 &&
            kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            $"{scenario}: Stop must drain all admission state");

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class TestConfigurationException : Exception
    {
    }

    private interface IMissingAdmissionContract : IService
    {
    }

    private sealed class UnsupportedServer : ISharpLinkServer
    {
        public SharpLinkHealthStatus HealthStatus => default;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RunAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}