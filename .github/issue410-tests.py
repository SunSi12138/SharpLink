from pathlib import Path

server_tests = Path('test/SharpLink.UnitTests/Server')

migration_only = [
    'AdmissionDynamicRateLegacyWaiterRegressionTests.cs',
    'AdmissionDynamicRateLineageAndLifecycleTests.cs',
    'AdmissionDynamicRateReplacementRegressionTests.cs',
    'AdmissionDynamicRateReviewRegressionTests.cs',
    'AdmissionDynamicRateSemanticsTests.cs',
    'AdmissionDynamicRateTransitionCarryRegressionTests.cs',
    'AdmissionDynamicRateUpdateTests.cs',
    'AdmissionDynamicPartitionRateTransitionTests.cs',
]
for name in migration_only:
    path = server_tests / name
    if not path.exists():
        raise RuntimeError(f'missing expected migration-only test file: {path}')
    path.unlink()

candidate_tests = r'''using System.Net;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionGenerationScopedRateTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> TenantSelector =
        static _ => "tenant-a";

    [Test]
    public async Task UnchangedRateDefinitionShouldReuseExactStateAndHistory()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureTokenBucket(options.Global, 1, 1, 10));
        await ConsumeAsync(source, 1);
        var sourceState = source.Controller.GlobalRateStateForTests!;

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureTokenBucket(options.Global, 1, 1, 10));

        Ensure(ReferenceEquals(sourceState, replacement.Controller.GlobalRateStateForTests),
            "an unchanged rate definition should reuse the exact steady-state limiter");
        await EnsureRejectedAsync(replacement, "rate",
            "a no-op rate update must not manufacture fresh quota");
        replacement.Retire();
    }

    [Test]
    public async Task ChangedTokenBucketShouldStartFreshWhileCapturedOldGenerationRemainsIndependent()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureTokenBucket(options.Global, 1, 1, 60));
        Ensure(source.TryAcquireUse(), "old request must retain generation N across publication");
        await ConsumeAsync(source, 1);
        var sourceState = source.Controller.GlobalRateStateForTests!;

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureTokenBucket(options.Global, 2, 2, 60));
        var targetState = replacement.Controller.GlobalRateStateForTests!;

        Ensure(!ReferenceEquals(sourceState, targetState),
            "changed rate policy must publish a distinct generation-local state");
        await ConsumeAsync(replacement, 2);
        await EnsureRejectedAsync(replacement, "rate",
            "target generation should enforce only its own two-permit quota");
        await EnsureRejectedAsync(source, "rate",
            "old captured generation must continue under its already-consumed source quota");
        Ensure(kernel.RateStateCount == 2,
            "old and new rate generations must coexist only while old captured work remains live");

        source.ReleaseUse();
        Ensure(source.IsReclaimed && kernel.RateStateCount == 1,
            "old rate generation must reclaim as soon as its final captured use drains");
        replacement.Retire();
    }

    [Test]
    [Arguments(RateKind.TokenBucket, RateKind.FixedWindow)]
    [Arguments(RateKind.FixedWindow, RateKind.SlidingWindow)]
    [Arguments(RateKind.SlidingWindow, RateKind.TokenBucket)]
    public async Task AlgorithmReplacementShouldUseFreshTargetGeneration(
        RateKind sourceKind,
        RateKind targetKind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureRate(options.Global, sourceKind, 1));
        await ConsumeAsync(source, 1);

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureRate(options.Global, targetKind, 1));

        await ConsumeAsync(replacement, 1);
        await EnsureRejectedAsync(replacement, "rate",
            $"{sourceKind} -> {targetKind} target must enforce its own fresh generation quota");
        Ensure(kernel.RateStateCount == 1,
            "an unreferenced replaced rate state should reclaim immediately");
        replacement.Retire();
    }

    [Test]
    public async Task OldQueuedRateWaiterShouldDrainOnSourceWithoutChargingTarget()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options.Global, 1, 1, 10);
        });
        Ensure(source.TryAcquireUse(), "queued old request must retain generation N");
        await ConsumeAsync(source, 1, allowQueue: true);
        var queued = source.Controller.AcquireAsync(
            Context(), 7, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 7 && time.ActiveTimerCount > 0,
            "old waiter must own one server queue reservation and one source timer");

        var replacement = CommitUpdate(kernel, source, options =>
        {
            ConfigureQueue(options);
            ConfigureFixedWindow(options.Global, 1, 3600);
        });
        await ConsumeAsync(replacement, 1);
        await EnsureRejectedAsync(replacement, "rate",
            "target fixed window must be independently exhausted after its own permit");

        time.Advance(TimeSpan.FromSeconds(10));
        var oldDecision = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(oldDecision.IsAcquired,
            "old queued request must remain valid and grant under captured source cadence");
        oldDecision.Lease!.Dispose();
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0,
            "old waiter completion must release outer queue accounting exactly once");
        await EnsureRejectedAsync(replacement, "rate",
            "late source grant must not be translated or charged into the target generation");

        source.ReleaseUse();
        Ensure(source.IsReclaimed && kernel.RateStateCount == 1 && time.ActiveTimerCount == 0,
            "source state and timer must reclaim after the final old captured use drains");
        replacement.Retire();
    }

    [Test]
    public async Task OldQueuedCancellationAfterReplacementShouldReleaseQueueExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options.Global, 1, 1, 40);
        });
        Ensure(source.TryAcquireUse(), "queued old request must retain generation N");
        await ConsumeAsync(source, 1, allowQueue: true);
        using var cancellation = new CancellationTokenSource();
        var queued = source.Controller.AcquireAsync(
            Context(), 9, allowQueue: true, cancellation.Token).AsTask();
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 9,
            "source waiter must reserve one authoritative queue entry");

        var replacement = CommitUpdate(kernel, source, options =>
        {
            ConfigureQueue(options);
            ConfigureSlidingWindow(options.Global, 1, 60, 3);
        });

        cancellation.Cancel();
        Ensure(await CaptureAsyncFailure(queued) is OperationCanceledException,
            "old-generation waiter cancellation must preserve cancellation semantics");
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0,
            "cancellation must return server queue count/bytes exactly once");
        await ConsumeAsync(replacement, 1);
        await EnsureRejectedAsync(replacement, "rate",
            "cancelled old waiter must not affect target generation quota");

        source.ReleaseUse();
        Ensure(source.IsReclaimed && kernel.RateStateCount == 1,
            "cancelled source generation must reclaim after retained use ends");
        replacement.Retire();
    }

    [Test]
    public async Task FixedWindowRolloverShouldRemainLocalToEachGeneration()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureFixedWindow(options.Global, 1, 10));
        Ensure(source.TryAcquireUse(), "source fixed window must remain captured across update");
        await ConsumeAsync(source, 1);
        time.Advance(TimeSpan.FromSeconds(10).Subtract(TimeSpan.FromTicks(1)));

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFixedWindow(options.Global, 2, 20));
        await ConsumeAsync(replacement, 2);
        await EnsureRejectedAsync(replacement, "rate",
            "target window must start fresh at publication and enforce its own two permits");
        await EnsureRejectedAsync(source, "rate",
            "source must remain exhausted one tick before its own rollover");

        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(source, 1);
        await EnsureRejectedAsync(replacement, "rate",
            "source rollover must not replenish or mutate the target window");

        source.ReleaseUse();
        replacement.Retire();
    }

    [Test]
    public async Task SlidingSegmentBoundariesShouldRemainLocalToEachGeneration()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureSlidingWindow(options.Global, 1, 8, 4));
        Ensure(source.TryAcquireUse(), "source sliding window must remain captured across update");
        await ConsumeAsync(source, 1);
        time.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureSlidingWindow(options.Global, 2, 10, 5));
        await ConsumeAsync(replacement, 2);
        await EnsureRejectedAsync(replacement, "rate",
            "target sliding generation must enforce only its own history");

        time.Advance(TimeSpan.FromTicks(1));
        await EnsureRejectedAsync(source, "rate",
            "crossing one source segment boundary must not erase its active-window history");
        time.Advance(TimeSpan.FromSeconds(6));
        await ConsumeAsync(source, 1);
        await EnsureRejectedAsync(replacement, "rate",
            "source history expiry must not remap or mutate target segments");

        source.ReleaseUse();
        replacement.Retire();
    }

    [Test]
    public void RepeatedStructuralRateUpdatesShouldRemainBoundedAndTimerFree()
    {
        var time = new ManualTimeProvider();
        using var owner = new KernelOwner(new AdmissionStateKernel(time));
        var kernel = owner.Kernel;
        var current = CreateProgram(kernel, options => ConfigureRate(options.Global, RateKind.TokenBucket, 4));

        for (var index = 0; index < 96; index++)
        {
            var kind = (RateKind)((index + 1) % 3);
            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureRate(options.Global, kind, 4 + (index & 1)));
            Ensure(kernel.RateStateCount == 1 && kernel.LiveProgramCount == 1 &&
                   kernel.RetiredProgramCount == 0,
                $"update {index}: drained generations must not accumulate state or retired programs");
            Ensure(time.ActiveTimerCount == 0,
                $"update {index}: immediate-only updates must not leave replenishment timers active");
        }

        current.Retire();
        Ensure(kernel.RateStateCount == 0 && kernel.LiveProgramCount == 0 && time.ActiveTimerCount == 0,
            "final retirement must return repeated-update state to zero");
    }

    [Test]
    public async Task ConcurrencyShrinkShouldRemainContinuousAcrossRateGenerationReplacement()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureComposite(options, 2, RateKind.TokenBucket));
        var first = await source.Controller.AcquireAsync(Context(), 1, false, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(Context(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired,
            "source must hold two active concurrency permits before shrink");

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureComposite(options, 1, RateKind.FixedWindow));
        await EnsureRejectedAsync(replacement, "concurrency",
            "rate generation replacement must not bypass a concurrency shrink");

        first.Lease!.Dispose();
        await EnsureRejectedAsync(replacement, "concurrency",
            "active count equal to the shrunken target must still block new acquisition");
        second.Lease!.Dispose();
        await ConsumeAsync(replacement, 1);
        replacement.Retire();
    }

    [Test]
    public async Task DisableEnableShouldCreateFreshRateGenerationWhileOldCaptureDrains()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureTokenBucket(options.Global, 1, 1, 3600));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "source must remain captured through disable/re-enable");
        await ConsumeAsync(source, 1);
        var sourceState = source.Controller.GlobalRateStateForTests!;

        publicServer.DisableAdmissionControl();
        publicServer.EnableAdmissionControl(options => ConfigureTokenBucket(options.Global, 1, 1, 3600));
        var reenabled = Current(server);

        Ensure(!ReferenceEquals(sourceState, reenabled.Controller.GlobalRateStateForTests),
            "re-enable is a new publication and must not resurrect a draining historical rate state");
        await ConsumeAsync(reenabled, 1);
        await EnsureRejectedAsync(source, "rate",
            "captured old generation must retain its own exhausted quota after re-enable");
        source.ReleaseUse();
        Ensure(source.IsReclaimed,
            "disabled historical generation must reclaim when its final captured request drains");
    }

    [Test]
    public async Task PartitionStructuralRateUpdateShouldUseExistingPolicyGenerationLifetimeWithoutHistoryMigration()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigurePartition(options, RateKind.TokenBucket, 1));
        Ensure(source.TryAcquireUse(), "old partition policy generation must remain captured across update");
        await ConsumeAsync(source, 1);
        var pool = source.Controller.PartitionStateForTests!;

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigurePartition(options, RateKind.FixedWindow, 1));

        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "partition namespace ownership should remain stable across structural rate replacement");
        Ensure(kernel.PartitionRuntimeGenerationCount >= 2,
            "existing partition runtime generations should keep old captured policy state alive");
        await ConsumeAsync(replacement, 1);
        await EnsureRejectedAsync(replacement, "rate",
            "target partition generation must enforce its own fresh quota");
        await EnsureRejectedAsync(source, "rate",
            "old captured partition generation must remain independently exhausted");

        source.ReleaseUse();
        Ensure(source.IsReclaimed,
            "old partition program must reclaim after the captured request drains");
        replacement.Retire();
    }

    [Test]
    public async Task StopShouldDrainOldAndNewRateGenerationsAndReturnQueueAccountingToZero()
    {
        var time = new ManualTimeProvider();
        var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options.Global, 1, 1, 60);
        });
        Ensure(source.TryAcquireUse(), "old queued request must retain source generation until stop");
        await ConsumeAsync(source, 1, allowQueue: true);
        var queued = source.Controller.AcquireAsync(
            Context(), 11, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 11 && time.ActiveTimerCount > 0,
            "old generation must own one queued waiter and active timer before replacement");

        var replacement = CommitUpdate(kernel, source, options =>
        {
            ConfigureQueue(options);
            ConfigureFixedWindow(options.Global, 2, 3600);
        });
        await ConsumeAsync(replacement, 1);

        var disposeTask = kernel.DisposeAsync().AsTask();
        var oldDecision = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!oldDecision.IsAcquired && oldDecision.Reason == "draining",
            "stop must terminate old-generation queued work with draining semantics");
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0,
            "stop must release authoritative queue accounting exactly once");

        source.ReleaseUse();
        await disposeTask;
        Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
               kernel.RateStateCount == 0 && kernel.QueuedCalls == 0 &&
               kernel.QueuedBytes == 0 && kernel.ActivePermits == 0 && time.ActiveTimerCount == 0,
            "stop must reclaim all old/new rate generations and timers exactly once");
    }

    private static AdmissionProgram CreateProgram(
        AdmissionStateKernel kernel,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        return kernel.CreateProgram(options, []);
    }

    private static AdmissionProgram CommitUpdate(
        AdmissionStateKernel kernel,
        AdmissionProgram source,
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        var replacement = kernel.CreateUpdateProgram(source, options, [], out var plan);
        try
        {
            if (plan.RequiresTargetCommit)
            {
                kernel.BeginConcurrencyTargetCommit();
                try
                {
                    plan.Commit();
                }
                finally
                {
                    kernel.CompleteConcurrencyTargetCommit();
                }
                replacement.Controller.GrantConcurrencyWaitersAfterTargetCommit();
            }
            else
            {
                plan.Commit();
            }
            source.Retire();
            return replacement;
        }
        catch
        {
            replacement.Retire();
            throw;
        }
    }

    private static void ConfigureComposite(
        SharpLinkAdmissionControlOptions options,
        int concurrency,
        RateKind rateKind)
    {
        options.Global.UseConcurrency(concurrency);
        ConfigureRate(options.Global, rateKind, 100);
    }

    private static void ConfigurePartition(
        SharpLinkAdmissionControlOptions options,
        RateKind kind,
        int limit)
    {
        options.UsePartition(TenantSelector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            ConfigureRate(partition, kind, limit);
        });
    }

    private static void ConfigureRate(SharpLinkAdmissionRuleOptions rule, RateKind kind, int limit)
    {
        switch (kind)
        {
            case RateKind.TokenBucket:
                ConfigureTokenBucket(rule, limit, limit, 3600);
                break;
            case RateKind.FixedWindow:
                ConfigureFixedWindow(rule, limit, 3600);
                break;
            case RateKind.SlidingWindow:
                ConfigureSlidingWindow(rule, limit, 3600, 4);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void ConfigureTokenBucket(
        SharpLinkAdmissionRuleOptions rule,
        int limit,
        int tokensPerPeriod,
        int periodSeconds)
        => rule.UseTokenBucket(rate =>
        {
            rate.TokenLimit = limit;
            rate.TokensPerPeriod = tokensPerPeriod;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds);
        });

    private static void ConfigureFixedWindow(
        SharpLinkAdmissionRuleOptions rule,
        int limit,
        int windowSeconds)
        => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(windowSeconds);
        });

    private static void ConfigureSlidingWindow(
        SharpLinkAdmissionRuleOptions rule,
        int limit,
        int windowSeconds,
        int segments)
        => rule.UseSlidingWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(windowSeconds);
            rate.SegmentsPerWindow = segments;
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 4096;
        options.MaxQueueDelay = TimeSpan.FromMinutes(2);
    }

    private static async Task ConsumeAsync(
        AdmissionProgram program,
        int count,
        bool allowQueue = false)
    {
        for (var index = 0; index < count; index++)
        {
            var decision = await program.Controller.AcquireAsync(
                Context(), 1, allowQueue, CancellationToken.None);
            Ensure(decision.IsAcquired, $"expected permit {index + 1} of {count} to be acquired");
            decision.Lease!.Dispose();
        }
    }

    private static async Task EnsureRejectedAsync(
        AdmissionProgram program,
        string reason,
        string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == reason, scenario);
    }

    private static async Task<Exception?> CaptureAsyncFailure(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext Context()
        => new(101, 202, RpcMethodKind.Unary, "generation-scoped-rate", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum RateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }

    private sealed class KernelOwner(AdmissionStateKernel kernel) : IDisposable
    {
        internal AdmissionStateKernel Kernel { get; } = kernel;

        public void Dispose()
            => Kernel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
'''

(server_tests / 'AdmissionGenerationScopedRateTests.cs').write_text(candidate_tests, encoding='utf-8')
