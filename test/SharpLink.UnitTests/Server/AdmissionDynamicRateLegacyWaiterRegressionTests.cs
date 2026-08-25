using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateLegacyWaiterRegressionTests
{
    [Test]
    public async Task OldFixedWindowWaiterGrantShouldRemainDebtOnFastTokenBucketTarget()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureFixedWindowQueue(options));
        Ensure(source.TryAcquireUse(), "test must retain the old program while its waiter survives publication");

        var consumed = await source.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        Ensure(consumed.IsAcquired, "source fixed window must grant its first permit");
        consumed.Lease!.Dispose();

        var oldQueued = source.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(kernel.QueuedCalls == 1 && source.Controller.GlobalRateStateForTests!.WaitingCount == 1,
            "old request must own exactly one kernel queue reservation and one source rate waiter");

        var replacement = CreateUpdate(
            kernel,
            source,
            options => ConfigureFastTokenBucketQueue(options),
            out var plan);
        plan.Commit();
        source.Retire();

        time.Advance(TimeSpan.FromSeconds(40));
        var oldDecision = await oldQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(oldDecision.IsAcquired,
            "old fixed-window waiter must remain valid and grant when its captured source window rolls");
        oldDecision.Lease!.Dispose();
        Ensure(kernel.QueuedCalls == 0 && source.Controller.GlobalRateStateForTests!.WaitingCount == 0,
            "old waiter completion must release its outer queue reservation exactly once");

        await EnsureRateRejectedAsync(replacement,
            "the target must account for the old-generation grant at the handoff timestamp");
        time.Advance(TimeSpan.FromSeconds(1));
        await EnsureRateRejectedAsync(replacement,
            "a one-second target replenishment must not erase a grant that belongs to the old forty-second fixed window");
        time.Advance(TimeSpan.FromSeconds(39).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "legacy waiter debt must remain effective one tick before the old grant's conservative expiry");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement);

        source.ReleaseUse();
        Ensure(source.IsReclaimed && kernel.RateStateCount == 1,
            "retired source state must reclaim after the retained old-generation use ends");
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

    private static void ConfigureFixedWindowQueue(SharpLinkAdmissionControlOptions options)
    {
        ConfigureQueue(options);
        options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = 1;
            rate.Window = TimeSpan.FromSeconds(40);
        });
    }

    private static void ConfigureFastTokenBucketQueue(SharpLinkAdmissionControlOptions options)
    {
        ConfigureQueue(options);
        options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1;
            rate.TokensPerPeriod = 1;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        });
    }

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 1;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(2);
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static async Task ConsumeAsync(AdmissionProgram program)
    {
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(decision.IsAcquired, "expected target rate permit to become available");
        decision.Lease!.Dispose();
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-legacy-waiter", null, null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
