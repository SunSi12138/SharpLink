using System.Net;
using System.Linq;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateLineageAndLifecycleTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> TenantSelector =
        static _ => "tenant-a";

    [Test]
    public async Task GlobalContractAndMethodRateAddRemoveShouldRetireOldStatesOnlyAfterOldUseEnds()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var initial = CreateProgram(kernel, options => options.Global.UseConcurrency(8));
        var added = CommitUpdate(kernel, initial, ConfigureScopedRates);

        Ensure(FindRate(added, AdmissionRuleStateKey.Global) is not null &&
               FindRate(added, AdmissionRuleStateKey.Contract(101)) is not null &&
               FindRate(added, AdmissionRuleStateKey.Method(101, 202)) is not null &&
               kernel.RateStateCount == 3,
            "rate add must create independent Global, Contract, and Method logical components");
        Ensure(added.TryAcquireUse(), "test must retain the exact rate-bearing generation across removal");

        var removed = CreateUpdate(
            kernel,
            added,
            options => options.Global.UseConcurrency(8),
            out var removalPlan);
        removalPlan.Commit();
        added.Retire();
        Ensure(FindRate(removed, AdmissionRuleStateKey.Global) is null &&
               removed.Controller.RuleStateBindings.Count == 1 &&
               kernel.RateStateCount == 3,
            "N+1 omission must remove all rate components while old captured N keeps their states alive");

        var oldDecision = await added.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(oldDecision.IsAcquired,
            "old captured generation must remain usable after rate removal publication");
        oldDecision.Lease!.Dispose();

        added.ReleaseUse();
        Ensure(added.IsReclaimed && kernel.RateStateCount == 0,
            "removed rate states must reclaim only after the final old-generation use ends");
    }

    [Test]
    public void RemoveReaddDisableEnableLineageShouldReuseCurrentBNotHistoricalA()
    {
        var time = new ManualTimeProvider();
        using var kernelOwner = new KernelOwner(new AdmissionStateKernel(time));
        var kernel = kernelOwner.Kernel;
        var a = CreateProgram(kernel, ConfigureSingleTokenBucket);
        kernel.RecordPublishedRateLineage(a.Controller);
        var aState = a.Controller.GlobalRateStateForTests!;
        Ensure(a.TryAcquireUse(), "historical A must remain alive across remove/re-add overlap");

        var removed = CreateUpdate(
            kernel,
            a,
            options => options.Global.UseConcurrency(1),
            out var removePlan);
        removePlan.Commit();
        kernel.RecordPublishedRateLineage(removed.Controller);
        a.Retire();

        var b = CreateUpdate(kernel, removed, options =>
        {
            options.Global.UseConcurrency(1);
            ConfigureSingleTokenBucket(options);
        }, out var addPlan);
        addPlan.Commit();
        kernel.RecordPublishedRateLineage(b.Controller);
        removed.Retire();
        var bState = b.Controller.GlobalRateStateForTests!;
        Ensure(!ReferenceEquals(aState, bState),
            "re-add after a real removal must create fresh/current lineage B instead of historical A");
        Ensure(b.TryAcquireUse(), "B must stay alive across the simulated disabled interval");

        kernel.RecordPublishedRateLineage(b.Controller);
        b.Retire();
        var reenabled = CreateProgram(kernel, options =>
        {
            options.Global.UseConcurrency(1);
            ConfigureSingleTokenBucket(options);
        });
        var reenabledState = reenabled.Controller.GlobalRateStateForTests!;
        Ensure(ReferenceEquals(bState, reenabledState) && !ReferenceEquals(aState, reenabledState),
            "compatible enable after disable must select current published B lineage, never historical A");

        a.ReleaseUse();
        b.ReleaseUse();
        Ensure(a.IsReclaimed && b.IsReclaimed && kernel.RateStateCount == 1,
            "historical overlapping states must reclaim while the reenabled current state remains bounded");
    }

    [Test]
    [NotInParallel]
    public async Task LosingRateUpdateMustNotMutateSourceOrWinningTargetQuota()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureTokenBucket(options, 1));
        var source = Current(server);
        var consumed = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "source token must be consumed before competing candidates are prepared");
        consumed.Lease!.Dispose();
        var sourceState = source.Controller.GlobalRateStateForTests!;
        using var loserAtWriter = new ManualResetEventSlim();
        using var releaseLoser = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) ||
                    candidate?.Controller.GlobalRateStateForTests?.Definition.Limit != 2)
                {
                    return;
                }
                loserAtWriter.Set();
                if (!releaseLoser.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("rate losing-writer barrier timed out");
            };

            var loser = Task.Run(() => CaptureFailure(() =>
                publicServer.UpdateAdmissionControl(options => ConfigureTokenBucket(options, 2))));
            Ensure(loserAtWriter.Wait(TimeSpan.FromSeconds(5)),
                "candidate A must finish construction outside the writer lock");
            Ensure(sourceState.TransitionDebtForDiagnostics == 1,
                "speculative rate candidate construction must not mutate source quota");

            var winnerFailure = CaptureFailure(() =>
                publicServer.UpdateAdmissionControl(options => ConfigureTokenBucket(options, 3)));
            Ensure(winnerFailure is null, "candidate B must publish while candidate A remains speculative");
            releaseLoser.Set();
            Ensure(await loser.WaitAsync(TimeSpan.FromSeconds(5)) is InvalidOperationException,
                "candidate A must lose exact-source validation rather than auto-rebase");

            var winner = Current(server);
            var winnerState = winner.Controller.GlobalRateStateForTests!;
            Ensure(winnerState.Definition.Limit == 3 && winnerState.TransitionDebtForDiagnostics == 1,
                "winning target must inherit exactly the source debt, with no losing-candidate mutation");
            await ConsumeAsync(winner, 2);
            await EnsureRateRejectedAsync(winner,
                "winner limit three must expose only the two permits remaining after preserved source debt");
            Ensure(winner.Kernel.RateStateCount == 1 && winner.Kernel.LiveProgramCount == 1,
                "losing candidate and retired source rate states must fully reclaim");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            releaseLoser.Set();
        }
    }

    [Test]
    public async Task RetainedOldRateLeaseAcrossDownstreamConcurrencyShouldNotBeChargedTwiceAfterUpdate()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureCompositeQueue(options, rateLimit: 1));
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        using var blocker = contract.AttemptAcquire(1);
        Ensure(blocker.IsAcquired,
            "test must occupy downstream Contract concurrency without consuming Global rate");

        var oldQueued = source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None).AsTask();
        Ensure(kernel.QueuedCalls == 1 && contract.WaitingCount == 1,
            "old request must retain its consumed Global rate lease while waiting downstream");

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureCompositeQueue(options, rateLimit: 2));
        Ensure(replacement.Controller.GlobalRateStateForTests!.TransitionDebtForDiagnostics == 1,
            "N+1 must inherit the one rate permit retained by the old queued request");

        blocker.Dispose();
        var oldDecision = await oldQueued;
        Ensure(oldDecision.IsAcquired,
            "old request must finish with its retained source rate lease after N+1 publication");
        oldDecision.Lease!.Dispose();
        Ensure(kernel.QueuedCalls == 0 && contract.WaitingCount == 0,
            "old composite waiter must release queue accounting exactly once");

        await ConsumeAsync(replacement, 1);
        await EnsureRateRejectedAsync(replacement,
            "reusing the pre-update retained lease must not double-charge target debt or mint a second permit");
    }

    [Test]
    public async Task StopShouldCancelQueuedRateWaiterAndDrainRetiredTimerStateExactlyOnce()
    {
        var time = new ManualTimeProvider();
        var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options, 1);
        });
        Ensure(source.TryAcquireUse(), "queued request must keep its captured program alive through Stop");
        try
        {
            await ConsumeAsync(source, 1);
            var queued = source.Controller.AcquireAsync(
                CreateContext(), 9, true, CancellationToken.None).AsTask();
            Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 9 &&
                   source.Controller.GlobalRateStateForTests!.WaitingCount == 1 &&
                   time.ActiveTimerCount > 0,
                "queued rate Request must own kernel reservation, inner waiter, and replenishment timer");

            var disposeTask = kernel.DisposeAsync().AsTask();
            var decision = await queued;
            Ensure(!decision.IsAcquired && decision.ErrorCode == SharpLinkErrorCode.Unavailable &&
                   decision.Reason == "draining",
                "Stop must terminate the queued rate Request using shutdown semantics");
            Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0,
                "Stop cancellation must release outer queue accounting exactly once");

            source.ReleaseUse();
            await disposeTask;
            Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
                   kernel.RateStateCount == 0 && kernel.QueuedCalls == 0 &&
                   kernel.QueuedBytes == 0 && kernel.ActivePermits == 0 &&
                   time.ActiveTimerCount == 0,
                "Stop must drain current/retired rate state and dispose timers exactly once");
        }
        finally
        {
            if (source.ActiveUses != 0)
                source.ReleaseUse();
            await kernel.DisposeAsync();
        }
    }

    [Test]
    public async Task UnchangedPartitionPoolAndQuotaShouldSurviveGlobalRateReplacement()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigurePartitionAndGlobalRate(options, tokenBucket: true));
        var pool = source.Controller.PartitionStateForTests!;
        var consumed = await source.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "source request must consume partition quota before global rate update");
        consumed.Lease!.Dispose();

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigurePartitionAndGlobalRate(options, tokenBucket: false));
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "supported non-partition rate replacement must reuse the exact partition pool");
        var rejected = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == "rate" && rejected.Scope == "partition",
            "consumed partition quota must not reset during unrelated Global algorithm replacement");
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

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
        var replacement = CreateUpdate(kernel, source, configure, out var plan);
        plan.Commit();
        return replacement;
    }

    private static AdmissionProgram CreateUpdate(
        AdmissionStateKernel kernel,
        AdmissionProgram source,
        Action<SharpLinkAdmissionControlOptions> configure,
        out AdmissionUpdatePlan plan)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        options.Validate();
        return kernel.CreateUpdateProgram(source, options, [], out plan);
    }

    private static AdmissionRateState? FindRate(AdmissionProgram program, AdmissionRuleStateKey key)
        => program.Controller.RuleStateBindings.FirstOrDefault(binding => binding.Key == key).RateState;

    private static void ConfigureScopedRates(SharpLinkAdmissionControlOptions options)
    {
        options.Global.UseConcurrency(8);
        ConfigureTokenBucket(options, 2);
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = 2;
            rate.Window = TimeSpan.FromSeconds(30);
        }));
        options.AddMethod(101, 202, rule => rule.UseSlidingWindow(rate =>
        {
            rate.PermitLimit = 2;
            rate.Window = TimeSpan.FromSeconds(30);
            rate.SegmentsPerWindow = 3;
        }));
    }

    private static void ConfigureSingleTokenBucket(SharpLinkAdmissionControlOptions options)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });

    private static void ConfigureTokenBucket(SharpLinkAdmissionControlOptions options, int limit)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = limit;
            rate.TokensPerPeriod = limit;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });

    private static void ConfigureCompositeQueue(
        SharpLinkAdmissionControlOptions options,
        int rateLimit)
    {
        ConfigureQueue(options);
        ConfigureTokenBucket(options, rateLimit);
        options.AddContract(101, rule => rule.UseConcurrency(1));
    }

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 2;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(2);
    }

    private static void ConfigurePartitionAndGlobalRate(
        SharpLinkAdmissionControlOptions options,
        bool tokenBucket)
    {
        if (tokenBucket)
        {
            ConfigureTokenBucket(options, 1);
        }
        else
        {
            options.Global.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 4;
                rate.Window = TimeSpan.FromSeconds(1);
            });
        }

        options.UsePartition(TenantSelector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromMinutes(10);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 1;
                rate.TokensPerPeriod = 1;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
        });
    }

    private static async Task ConsumeAsync(AdmissionProgram program, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var decision = await program.Controller.AcquireAsync(
                CreateContext(), 1, false, CancellationToken.None);
            Ensure(decision.IsAcquired, $"expected permit {index + 1} of {count} to be available");
            decision.Lease!.Dispose();
        }
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
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

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-lineage", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class KernelOwner(AdmissionStateKernel kernel) : IDisposable
    {
        internal AdmissionStateKernel Kernel { get; } = kernel;

        public void Dispose()
            => SharpLinkAsyncCleanup.DisposeSynchronously(Kernel);
    }
}