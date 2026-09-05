using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateReplacementRegressionTests
{
    [Test]
    [Arguments(RateAlgorithm.TokenBucket, RateAlgorithm.FixedWindow)]
    [Arguments(RateAlgorithm.TokenBucket, RateAlgorithm.SlidingWindow)]
    [Arguments(RateAlgorithm.FixedWindow, RateAlgorithm.TokenBucket)]
    [Arguments(RateAlgorithm.FixedWindow, RateAlgorithm.SlidingWindow)]
    [Arguments(RateAlgorithm.SlidingWindow, RateAlgorithm.TokenBucket)]
    [Arguments(RateAlgorithm.SlidingWindow, RateAlgorithm.FixedWindow)]
    public async Task AlgorithmReplacementShouldRetainSourceDebtUntilItsConservativeExpiry(
        RateAlgorithm sourceAlgorithm,
        RateAlgorithm targetAlgorithm)
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureSource(options, sourceAlgorithm));

        await ConsumeAsync(source, 4);
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFastTarget(options, targetAlgorithm));

        await EnsureRateRejectedAsync(replacement,
            $"{sourceAlgorithm} -> {targetAlgorithm}: replacement must begin behind the consumed source quota");
        time.Advance(TimeSpan.FromSeconds(1));
        await EnsureRateRejectedAsync(replacement,
            $"{sourceAlgorithm} -> {targetAlgorithm}: a one-second target cadence/window must not erase forty seconds of source debt");

        time.Advance(TimeSpan.FromSeconds(39).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            $"{sourceAlgorithm} -> {targetAlgorithm}: source debt must remain effective one tick before its conservative expiry");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement, 1);
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

    private static void ConfigureSource(
        SharpLinkAdmissionControlOptions options,
        RateAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case RateAlgorithm.TokenBucket:
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 4;
                    rate.TokensPerPeriod = 1;
                    rate.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
                });
                break;
            case RateAlgorithm.FixedWindow:
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 4;
                    rate.Window = TimeSpan.FromSeconds(40);
                });
                break;
            case RateAlgorithm.SlidingWindow:
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 4;
                    rate.Window = TimeSpan.FromSeconds(40);
                    rate.SegmentsPerWindow = 4;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
    }

    private static void ConfigureFastTarget(
        SharpLinkAdmissionControlOptions options,
        RateAlgorithm algorithm)
    {
        switch (algorithm)
        {
            case RateAlgorithm.TokenBucket:
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 4;
                    rate.TokensPerPeriod = 4;
                    rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
                });
                break;
            case RateAlgorithm.FixedWindow:
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 4;
                    rate.Window = TimeSpan.FromSeconds(1);
                });
                break;
            case RateAlgorithm.SlidingWindow:
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 4;
                    rate.Window = TimeSpan.FromSeconds(1);
                    rate.SegmentsPerWindow = 2;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
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
        var decision = await program.Controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate", scenario);
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-replacement", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum RateAlgorithm
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }
}
