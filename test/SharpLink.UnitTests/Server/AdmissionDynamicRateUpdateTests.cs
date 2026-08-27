using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateUpdateTests
{
    [Test]
    public async Task TokenBucketLimitUpdateShouldPreserveConsumedQuota()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureTokenBucket(options, 4, 1, TimeSpan.FromSeconds(10)));

        await ConsumeAsync(source, 3);
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureTokenBucket(options, 6, 1, TimeSpan.FromSeconds(10)));

        await ConsumeAsync(replacement, 3);
        await EnsureRateRejectedAsync(replacement,
            "raising TokenLimit from four to six after three consumed permits may expose only three permits, not a fresh six");
    }

    [Test]
    public async Task FixedWindowDurationUpdateShouldPreserveTheActiveWindowEpochAndConsumption()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureFixedWindow(options, 4, TimeSpan.FromSeconds(10)));

        await ConsumeAsync(source, 3);
        time.Advance(TimeSpan.FromSeconds(3));
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFixedWindow(options, 5, TimeSpan.FromSeconds(20)));

        await ConsumeAsync(replacement, 2);
        await EnsureRateRejectedAsync(replacement,
            "changing the fixed-window duration must not start a fresh window at publication");

        time.Advance(TimeSpan.FromSeconds(17).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "the preserved fixed-window epoch must remain exhausted one tick before its deterministic rollover");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement, 1);
    }

    [Test]
    public async Task SlidingWindowShapeUpdateShouldKeepHistoryThatRemainsInsideTheNewHorizon()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureSlidingWindow(options, 3, TimeSpan.FromSeconds(5), 2));

        await ConsumeAsync(source, 3);
        time.Advance(TimeSpan.FromSeconds(4));
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureSlidingWindow(options, 3, TimeSpan.FromSeconds(10), 5));

        time.Advance(TimeSpan.FromSeconds(1));
        await EnsureRateRejectedAsync(replacement,
            "history consumed at t=0 still belongs to the widened ten-second horizon after the old five-second horizon ends");
        time.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "segment remapping must retain still-active history until the new horizon expires");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement, 1);
    }

    [Test]
    public async Task AlgorithmReplacementShouldCarryAConservativeConsumedQuotaBarrier()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureTokenBucket(options, 4, 1, TimeSpan.FromSeconds(10)));

        await ConsumeAsync(source, 3);
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFixedWindow(options, 4, TimeSpan.FromSeconds(10)));

        await ConsumeAsync(replacement, 1);
        await EnsureRateRejectedAsync(replacement,
            "TokenBucket -> FixedWindow replacement must not layer a fresh four-permit target on top of three source permits");
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
        plan.Commit();
        source.Retire();
        return replacement;
    }

    private static async Task ConsumeAsync(AdmissionProgram program, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var decision = await program.Controller.AcquireAsync(
                CreateContext(), 1, allowQueue: false, CancellationToken.None);
            Ensure(decision.IsAcquired, $"expected permit {index + 1} of {count} to be available");
            decision.Lease!.Dispose();
        }
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var rejected = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        rejected.Lease?.Dispose();
        Ensure(!rejected.IsAcquired && rejected.Reason == "rate", scenario);
    }

    private static void ConfigureTokenBucket(
        SharpLinkAdmissionControlOptions options,
        int tokenLimit,
        int tokensPerPeriod,
        TimeSpan period)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = tokenLimit;
            rate.TokensPerPeriod = tokensPerPeriod;
            rate.ReplenishmentPeriod = period;
        });

    private static void ConfigureFixedWindow(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });

    private static void ConfigureSlidingWindow(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window,
        int segments)
        => options.Global.UseSlidingWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
            rate.SegmentsPerWindow = segments;
        });

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-update", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
