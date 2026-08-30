using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicPartitionRateTransitionTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> Selector =
        static _ => "tenant-a";

    [Test]
    public async Task PartitionTokenBucketCadenceChangesShouldNotReplenishAtPublication()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var current = CreateProgram(kernel, options => ConfigureTokenBucket(options, 3, 1, 10));
        try
        {
            await ConsumeAsync(current, 3);
            time.Advance(TimeSpan.FromSeconds(5));

            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureTokenBucket(options, 2, 2, 10));
            await EnsureRateRejectedAsync(current,
                "partition shrink below preserved token debt must not receive publication credit");
            time.Advance(TimeSpan.FromSeconds(10).Subtract(TimeSpan.FromTicks(1)));
            await EnsureRateRejectedAsync(current,
                "changed partition tokens-per-period must wait one complete target period");
            time.Advance(TimeSpan.FromTicks(1));
            await ConsumeAsync(current, 1);
            await EnsureRateRejectedAsync(current,
                "first partition replenishment may expose only quota justified after carried debt");

            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureTokenBucket(options, 2, 2, 5));
            await EnsureRateRejectedAsync(current,
                "partition replenishment-period update must not award an immediate extra period");
            time.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            await EnsureRateRejectedAsync(current,
                "partition target must retain a monotonic cadence anchor after publication");
            time.Advance(TimeSpan.FromTicks(1));
            await ConsumeAsync(current, 1);
        }
        finally
        {
            current.Retire();
        }
    }

    [Test]
    public async Task PartitionSlidingWindowShapeUpdateShouldRetainHistoricalConsumption()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var current = CreateProgram(kernel, options => ConfigureSlidingWindow(options, 3, 8, 4));
        try
        {
            await ConsumeAsync(current, 3);
            time.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));

            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureSlidingWindow(options, 2, 10, 5));
            await EnsureRateRejectedAsync(current,
                "partition shape update before a source segment boundary must retain consumption");
            time.Advance(TimeSpan.FromTicks(1));
            await EnsureRateRejectedAsync(current,
                "crossing the old segment boundary must not erase carried partition history");

            current = CommitUpdate(
                kernel,
                current,
                options => ConfigureSlidingWindow(options, 2, 6, 3));
            await EnsureRateRejectedAsync(current,
                "repeated partition shape remapping must not mint quota");
            time.Advance(TimeSpan.FromSeconds(8).Subtract(TimeSpan.FromTicks(1)));
            await EnsureRateRejectedAsync(current,
                "carried partition history must remain until the conservative horizon");
            time.Advance(TimeSpan.FromTicks(1));
            await ConsumeAsync(current, 1);
        }
        finally
        {
            current.Retire();
        }
    }

    [Test]
    public async Task OldPartitionRateWaiterCancellationShouldReleaseOuterReservationExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureTokenBucket(options, 1, 1, 40);
        });
        Ensure(source.TryAcquireUse(),
            "test must retain the old partition program while its rate waiter survives publication");
        AdmissionProgram current = source;

        try
        {
            await ConsumeAsync(source, 1, allowQueue: true);
            using var cancellation = new CancellationTokenSource();
            var queued = source.Controller.AcquireAsync(
                Context(), 7, allowQueue: true, cancellation.Token).AsTask();
            Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 7,
                "old partition rate waiter must own exactly one kernel count/byte reservation");

            current = CommitUpdate(kernel, source, options =>
            {
                ConfigureQueue(options);
                ConfigureFixedWindow(options, 1, 1);
            });

            cancellation.Cancel();
            Ensure(await CaptureAsyncFailure(queued) is OperationCanceledException,
                "old partition waiter must preserve cancellation semantics after N+1 publication");
            Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0,
                "partition waiter cancellation must release outer queue accounting exactly once");
            await EnsureRateRejectedAsync(current,
                "cancelling an old partition waiter must not erase quota consumed before update");
        }
        finally
        {
            if (source.ActiveUses != 0)
                source.ReleaseUse();
            current.Retire();
        }

        Ensure(source.IsReclaimed && kernel.PartitionRuntimeGenerationCount <= kernel.PartitionEntryCount,
            "old partition runtime generation must reclaim after the retained source use ends");
    }

    [Test]
    public async Task LateOldPartitionFixedWindowGrantShouldRemainDebtOnTokenBucketTarget()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var source = CreateProgram(kernel, options =>
        {
            ConfigureQueue(options);
            ConfigureFixedWindow(options, 1, 40);
        });
        Ensure(source.TryAcquireUse(),
            "test must retain the old partition generation while its waiter remains resident");
        AdmissionProgram current = source;

        try
        {
            await ConsumeAsync(source, 1, allowQueue: true);
            var oldQueued = source.Controller.AcquireAsync(
                Context(), 1, allowQueue: true, CancellationToken.None).AsTask();
            Ensure(kernel.QueuedCalls == 1,
                "old partition fixed-window waiter must own one outer queue reservation");

            current = CommitUpdate(kernel, source, options =>
            {
                ConfigureQueue(options);
                ConfigureTokenBucket(options, 1, 1, 1);
            });

            time.Advance(TimeSpan.FromSeconds(40));
            var oldDecision = await oldQueued;
            Ensure(oldDecision.IsAcquired,
                "old partition waiter must remain valid and grant when its captured source window rolls");
            oldDecision.Lease!.Dispose();
            Ensure(kernel.QueuedCalls == 0,
                "late old partition grant must release its outer queue reservation exactly once");

            await EnsureRateRejectedAsync(current,
                "target partition lineage must account for the old-generation grant at handoff time");
            time.Advance(TimeSpan.FromSeconds(1));
            await EnsureRateRejectedAsync(current,
                "fast target replenishment must not erase debt belonging to the old forty-second window");
            time.Advance(TimeSpan.FromSeconds(39).Subtract(TimeSpan.FromTicks(1)));
            await EnsureRateRejectedAsync(current,
                "legacy partition grant debt must remain one tick before conservative expiry");
            time.Advance(TimeSpan.FromTicks(1));
            await ConsumeAsync(current, 1);
        }
        finally
        {
            if (source.ActiveUses != 0)
                source.ReleaseUse();
            current.Retire();
        }

        Ensure(source.IsReclaimed,
            "retired source partition program must reclaim after its final retained use ends");
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

    private static void ConfigureTokenBucket(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int tokensPerPeriod,
        int periodSeconds)
    {
        options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = limit;
                rate.TokensPerPeriod = tokensPerPeriod;
                rate.ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds);
            });
        });
    }

    private static void ConfigureFixedWindow(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int windowSeconds)
    {
        options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseFixedWindow(rate =>
            {
                rate.PermitLimit = limit;
                rate.Window = TimeSpan.FromSeconds(windowSeconds);
            });
        });
    }

    private static void ConfigureSlidingWindow(
        SharpLinkAdmissionControlOptions options,
        int limit,
        int windowSeconds,
        int segments)
    {
        options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseSlidingWindow(rate =>
            {
                rate.PermitLimit = limit;
                rate.Window = TimeSpan.FromSeconds(windowSeconds);
                rate.SegmentsPerWindow = segments;
            });
        });
    }

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
                Context(), 1, allowQueue, CancellationToken.None);
            Ensure(decision.IsAcquired, $"expected partition rate permit {index + 1} of {count}");
            decision.Lease!.Dispose();
        }
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate" && decision.Scope == "partition", scenario);
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

    private static SharpLinkAdmissionContext Context()
        => new(101, 202, RpcMethodKind.Unary, "partition-rate-transition", null, null);

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
