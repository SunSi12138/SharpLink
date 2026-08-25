using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateTransitionCarryRegressionTests
{
    [Test]
    public async Task TokenBucketUpdateShouldPreserveCadenceThatElapsedWhileBucketWasFull()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(
            kernel,
            options => ConfigureTokenBucket(options, limit: 1, tokensPerPeriod: 1, periodSeconds: 10));

        time.Advance(TimeSpan.FromSeconds(9));
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureTokenBucket(options, limit: 2, tokensPerPeriod: 1, periodSeconds: 10));
        await ConsumeAsync(replacement, 2);

        time.Advance(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "same-cadence update must not replenish before the original ten-second cadence boundary");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement, 1);
    }

    [Test]
    [Arguments(CarriedBarrierTarget.TokenBucket)]
    [Arguments(CarriedBarrierTarget.FixedWindow)]
    public async Task SameAlgorithmUpdateAfterReplacementShouldKeepCarriedTransitionDebt(
        CarriedBarrierTarget targetKind)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureLongSource(options, targetKind));
        await ConsumeAsync(source, 1);

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFastTarget(options, targetKind, limit: 1));
        await EnsureRateRejectedAsync(replacement,
            $"{targetKind}: structural replacement must initially carry the consumed source quota");

        var resizedTarget = CommitUpdate(
            kernel,
            replacement,
            options => ConfigureFastTarget(options, targetKind, limit: 2));
        await ConsumeAsync(resizedTarget, 1);
        await EnsureRateRejectedAsync(resizedTarget,
            $"{targetKind}: same-algorithm parameter update must preserve the replacement barrier instead of exposing a fresh second permit");
    }

    [Test]
    public async Task SlidingWindowLimitOnlyUpdateShouldPreserveIndividualSegmentAging()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(
            kernel,
            options => ConfigureSlidingWindow(options, limit: 2, windowSeconds: 4, segments: 4));

        await ConsumeAsync(source, 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await ConsumeAsync(source, 1);
        time.Advance(TimeSpan.FromMilliseconds(500));

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureSlidingWindow(options, limit: 3, windowSeconds: 4, segments: 4));
        await ConsumeAsync(replacement, 1);
        await EnsureRateRejectedAsync(replacement,
            "limit-only sliding update may expose exactly the new capacity while preserving old segment history");

        time.Advance(TimeSpan.FromSeconds(2.5));
        await ConsumeAsync(replacement, 1);
    }

    [Test]
    public async Task SlidingWindowSegmentRoundingMustNotExpireQuotaBeforeConfiguredWindow()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var program = CreateProgram(
            kernel,
            options => ConfigureSlidingWindow(options, limit: 1, windowSeconds: 1, segments: 3));

        await ConsumeAsync(program, 1);
        time.Advance(TimeSpan.FromSeconds(1).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(program,
            "segment rounding must never make a one-second sliding-window permit expire one tick early");
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

    private static void ConfigureLongSource(
        SharpLinkAdmissionControlOptions options,
        CarriedBarrierTarget targetKind)
    {
        if (targetKind == CarriedBarrierTarget.TokenBucket)
        {
            options.Global.UseFixedWindow(rate =>
            {
                rate.PermitLimit = 1;
                rate.Window = TimeSpan.FromSeconds(40);
            });
            return;
        }

        ConfigureTokenBucket(options, limit: 1, tokensPerPeriod: 1, periodSeconds: 40);
    }

    private static void ConfigureFastTarget(
        SharpLinkAdmissionControlOptions options,
        CarriedBarrierTarget targetKind,
        int limit)
    {
        if (targetKind == CarriedBarrierTarget.TokenBucket)
        {
            ConfigureTokenBucket(options, limit, tokensPerPeriod: 1, periodSeconds: 1);
            return;
        }

        options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(1);
        });
    }

    private static void ConfigureTokenBucket(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int tokensPerPeriod,
        int periodSeconds)
        => options.Global.UseTokenBucket(rate =>
        {
            rate.TokenLimit = limit;
            rate.TokensPerPeriod = tokensPerPeriod;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds);
        });

    private static void ConfigureSlidingWindow(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int windowSeconds,
        int segments)
        => options.Global.UseSlidingWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(windowSeconds);
            rate.SegmentsPerWindow = segments;
        });

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
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-transition-carry", null, null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum CarriedBarrierTarget
    {
        TokenBucket,
        FixedWindow
    }
}
