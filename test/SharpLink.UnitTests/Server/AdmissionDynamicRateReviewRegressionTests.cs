using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateReviewRegressionTests
{
    [Test]
    public async Task MultipleLegacyTokenWaitersMustNotCollapseAccumulatedTargetDebtToOnePeriod()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, ConfigureFastSourceQueue);
        kernel.RecordPublishedRateLineage(source.Controller);
        Ensure(source.TryAcquireUse(), "old source generation must stay alive while its ten rate waiters late-grant");

        try
        {
            await ConsumeAsync(source);
            var waiters = new Task<AdmissionDecision>[10];
            for (var index = 0; index < waiters.Length; index++)
            {
                waiters[index] = source.Controller.AcquireAsync(
                    CreateContext(), 1, allowQueue: true, CancellationToken.None).AsTask();
            }
            Ensure(kernel.QueuedCalls == 10 && source.Controller.GlobalRateStateForTests!.WaitingCount == 10,
                "all ten old requests must own exactly one outer queue reservation and one source rate waiter each");

            var replacement = CreateUpdate(kernel, source, ConfigureSlowTargetQueue, out var plan);
            plan.Commit();
            kernel.RecordPublishedRateLineage(replacement.Controller);
            source.Retire();

            for (var index = 0; index < waiters.Length; index++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                var decision = await waiters[index].WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(decision.IsAcquired, $"old waiter {index + 1} must grant on its source one-second cadence");
                decision.Lease!.Dispose();
            }

            Ensure(kernel.QueuedCalls == 0 && source.Controller.GlobalRateStateForTests!.WaitingCount == 0,
                "all ten late grants must release their outer reservations and source waiters exactly once");
            Ensure(replacement.Controller.GlobalRateStateForTests!.TransitionDebtForDiagnostics >= 10,
                "after ten late grants, at least ten target debt units must remain at t=10s");

            time.Advance(TimeSpan.FromSeconds(10));
            Ensure(replacement.Controller.GlobalRateStateForTests!.TransitionDebtForDiagnostics >= 9,
                "a target replenishing one token per ten seconds cannot erase eleven accumulated debt units by t=20s");
        }
        finally
        {
            if (source.ActiveUses != 0)
                source.ReleaseUse();
        }
    }

    [Test]
    public async Task DisableEnableMustKeepCurrentRateLineageWhileHistoricalWaiterCanLateGrant()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var a = CreateProgram(kernel, ConfigureFastSourceQueue);
        kernel.RecordPublishedRateLineage(a.Controller);
        var aState = a.Controller.GlobalRateStateForTests!;
        Ensure(a.TryAcquireUse(), "historical A must remain alive across B retirement and re-enable");

        try
        {
            await ConsumeAsync(a);
            var oldQueued = a.Controller.AcquireAsync(
                CreateContext(), 1, allowQueue: true, CancellationToken.None).AsTask();
            Ensure(kernel.QueuedCalls == 1 && aState.WaitingCount == 1,
                "A must retain one queued waiter that can grant after B has no program references");

            var b = CreateUpdate(kernel, a, ConfigureSlowCurrentQueue, out var plan);
            plan.Commit();
            kernel.RecordPublishedRateLineage(b.Controller);
            a.Retire();
            var bState = b.Controller.GlobalRateStateForTests!;

            b.Retire();
            Ensure(b.IsReclaimed,
                "the B program may reclaim during the disabled interval because it has no captured users");
            Ensure(kernel.RateStateCount == 2,
                "B rate identity must stay anchored while historical A can still late-grant");

            var reenabled = CreateProgram(kernel, ConfigureSlowCurrentQueue);
            var reenabledState = reenabled.Controller.GlobalRateStateForTests!;
            Ensure(ReferenceEquals(bState, reenabledState),
                "same-policy enable must reuse current B even when the B program itself was already reclaimed");
            kernel.RecordPublishedRateLineage(reenabled.Controller);

            time.Advance(TimeSpan.FromSeconds(1));
            var oldDecision = await oldQueued.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(oldDecision.IsAcquired,
                "historical A waiter must remain valid and late-grant after re-enable");
            oldDecision.Lease!.Dispose();
            Ensure(kernel.QueuedCalls == 0 && aState.WaitingCount == 0,
                "historical waiter completion must release queue accounting exactly once");

            await EnsureRateRejectedAsync(reenabled,
                "A late grant must charge the re-enabled current B lineage instead of leaving fresh split quota");

            a.ReleaseUse();
            Ensure(a.IsReclaimed && kernel.RateStateCount == 1,
                "historical A and its state must reclaim once drained while re-enabled B remains current");
        }
        finally
        {
            if (a.ActiveUses != 0)
                a.ReleaseUse();
        }
    }

    [Test]
    public async Task TokenReplacementMustNotSpendOneReplenishmentOnBothCarriedAndTargetDebt()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, ConfigureThirtySecondFixedSource);

        await ConsumeAsync(source);
        await ConsumeAsync(source);
        await ConsumeAsync(source);

        var replacement = CreateUpdate(kernel, source, ConfigureCoupledTokenTarget, out var plan);
        plan.Commit();
        source.Retire();

        await ConsumeAsync(replacement);
        await EnsureRateRejectedAsync(replacement,
            "three carried permits plus the target t=0 grant must exhaust the four-permit target");

        time.Advance(TimeSpan.FromSeconds(10));
        await ConsumeAsync(replacement);
        await EnsureRateRejectedAsync(replacement,
            "the t=10 replenishment may be consumed only once while carried debt remains");

        time.Advance(TimeSpan.FromSeconds(10));
        await ConsumeAsync(replacement);
        await EnsureRateRejectedAsync(replacement,
            "the t=20 replenishment may be consumed only once while carried debt remains");

        time.Advance(TimeSpan.FromSeconds(10));
        await ConsumeAsync(replacement);
        await EnsureRateRejectedAsync(replacement,
            "at the t=30 carry horizon, the same replenishment cannot both repay carried debt and erase target-owned debt");
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

    private static void ConfigureFastSourceQueue(SharpLinkAdmissionControlOptions options)
    {
        ConfigureQueue(options, 10);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        });
    }

    private static void ConfigureSlowTargetQueue(SharpLinkAdmissionControlOptions options)
    {
        ConfigureQueue(options, 10);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 20;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });
    }

    private static void ConfigureSlowCurrentQueue(SharpLinkAdmissionControlOptions options)
    {
        ConfigureQueue(options, 10);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 2;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });
    }

    private static void ConfigureThirtySecondFixedSource(SharpLinkAdmissionControlOptions options)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = 4;
            rate.Window = TimeSpan.FromSeconds(30);
        });

    private static void ConfigureCoupledTokenTarget(SharpLinkAdmissionControlOptions options)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 4;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options, int maxQueuedCalls)
    {
        options.MaxQueuedCalls = maxQueuedCalls;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(2);
    }

    private static async Task ConsumeAsync(AdmissionProgram program)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(decision.IsAcquired, "expected rate permit to be available");
        decision.Lease!.Dispose();
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-review-regression", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
