using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicRateSemanticsTests
{
    [Test]
    public async Task TokenBucketShrinkAndCadenceChangesShouldNotReplenishAtPublication()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureTokenBucket(options, 3, 1, 10));
        await ConsumeAsync(source, 3);

        time.Advance(TimeSpan.FromSeconds(5));
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureTokenBucket(options, 2, 2, 10));
        await EnsureRateRejectedAsync(replacement,
            "shrinking below preserved token debt must block immediately without publication credit");
        time.Advance(TimeSpan.FromSeconds(10).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "changed tokens-per-period must not receive credit before one complete target period");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(replacement, 1);
        await EnsureRateRejectedAsync(replacement,
            "the first replenishment may expose only capacity justified by the preserved debt");

        var periodChanged = CommitUpdate(
            kernel,
            replacement,
            options => ConfigureTokenBucket(options, 2, 2, 5));
        await EnsureRateRejectedAsync(periodChanged,
            "changing replenishment period must not award an immediate extra period");
        time.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(periodChanged,
            "new replenishment period must retain a monotonic publication anchor");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(periodChanged, 1);
    }

    [Test]
    public async Task RepeatedTokenBucketUpdatesShouldNotAccumulateRoundingCredit()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var current = CreateProgram(kernel, options => ConfigureTokenBucket(options, 1, 1, 10));
        await ConsumeAsync(current, 1);

        for (var index = 0; index < 4; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureTokenBucket(options, 1, 1, 10));
            await EnsureRateRejectedAsync(current,
                "same-policy update must not mint fractional or rounded token credit");
        }

        time.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(current,
            "repeated updates must leave the original ten-second replenishment boundary intact");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(current, 1);
    }

    [Test]
    public async Task FixedWindowShrinkAndDurationIncreaseShouldSwitchAtOldBoundary()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureFixedWindow(options, 3, 10));
        await ConsumeAsync(source, 3);

        time.Advance(TimeSpan.FromSeconds(4));
        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFixedWindow(options, 2, 20));
        await EnsureRateRejectedAsync(replacement,
            "a changed Window keeps the exhausted old window authoritative until its natural boundary");

        time.Advance(TimeSpan.FromSeconds(6).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(replacement,
            "the target twenty-second Window must remain pending one tick before the old boundary");
        time.Advance(TimeSpan.FromTicks(1));

        await ConsumeAsync(replacement, 2);
        await EnsureRateRejectedAsync(replacement,
            "the new twenty-second Window must begin with exactly the two-permit target");
    }

    [Test]
    public async Task FixedWindowLimitIncreaseShouldExposeOnlyDifferenceInSameWindow()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options => ConfigureFixedWindow(options, 2, 10));
        await ConsumeAsync(source, 2);

        var replacement = CommitUpdate(
            kernel,
            source,
            options => ConfigureFixedWindow(options, 3, 10));
        await ConsumeAsync(replacement, 1);
        await EnsureRateRejectedAsync(replacement,
            "limit two to three may expose exactly one additional permit in the preserved window");
    }

    [Test]
    public async Task SlidingWindowShapeUpdatesAtSegmentBoundaryShouldRetainHistory()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var current = CreateProgram(kernel, options => ConfigureSlidingWindow(options, 3, 8, 4));
        await ConsumeAsync(current, 3);

        time.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));
        current = CommitUpdate(
            kernel,
            current,
            options => ConfigureSlidingWindow(options, 2, 10, 5));
        await EnsureRateRejectedAsync(current,
            "shape update immediately before a source segment boundary must retain consumed history");
        time.Advance(TimeSpan.FromTicks(1));
        await EnsureRateRejectedAsync(current,
            "crossing the old segment boundary after publication must not erase carried history");

        current = CommitUpdate(
            kernel,
            current,
            options => ConfigureSlidingWindow(options, 2, 6, 3));
        await EnsureRateRejectedAsync(current,
            "repeated remapping at the boundary must not mint quota");
        time.Advance(TimeSpan.FromSeconds(8).Subtract(TimeSpan.FromTicks(1)));
        await EnsureRateRejectedAsync(current,
            "carried sliding history must remain until the conservative ten-second horizon");
        time.Advance(TimeSpan.FromTicks(1));
        await ConsumeAsync(current, 1);
    }

    [Test]
    public async Task OldRateWaiterCancellationAfterUpdateShouldReleaseOuterReservationExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var kernel = new AdmissionStateKernel(time);
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options, 1, 1, 40);
        });
        Ensure(source.TryAcquireUse(), "test must retain the old generation while its waiter is resident");

        try
        {
            await ConsumeAsync(source, 1, allowQueue: true);
            using var cancellation = new CancellationTokenSource();
            var queued = source.Controller.AcquireAsync(
                CreateContext(), 7, allowQueue: true, cancellation.Token).AsTask();
            Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 7 &&
                   source.Controller.GlobalRateStateForTests!.WaitingCount == 1,
                "old rate waiter must own exactly one kernel count/byte reservation");

            var replacement = CreateUpdate(kernel, source, options =>
            {
                ConfigureQueue(options);
                ConfigureFixedWindow(options, 1, 1);
            }, out var plan);
            plan.Commit();
            source.Retire();

            cancellation.Cancel();
            Ensure(await CaptureAsyncFailure(queued) is OperationCanceledException,
                "old-generation rate waiter must preserve cancellation semantics after N+1 publication");
            Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 &&
                   source.Controller.GlobalRateStateForTests!.WaitingCount == 0,
                "cancellation must release the outer reservation and source waiter exactly once");

            await ConsumeAsync(replacement, 1);
            await EnsureRateRejectedAsync(replacement,
                "TokenBucket -> FixedWindow starts a fresh target generation while cancelled old work remains isolated");
        }
        finally
        {
            if (source.ActiveUses != 0)
                source.ReleaseUse();
        }

        Ensure(source.IsReclaimed && kernel.RateStateCount == 1,
            "old rate state must reclaim after its final retained generation use ends");
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

    private static void ConfigureFixedWindow(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int windowSeconds)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromSeconds(windowSeconds);
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

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 2;
        options.MaxQueuedBytes = 1024;
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
                CreateContext(), 1, allowQueue, CancellationToken.None);
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

    private static SharpLinkAdmissionContext CreateContext()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-rate-semantics", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
