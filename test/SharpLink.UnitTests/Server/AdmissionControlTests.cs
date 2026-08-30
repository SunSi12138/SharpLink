using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionControlTests
{
    [Test]
    public void QueueBoundsMustBeConfiguredTogether()
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1
        };
        options.Global.UseConcurrency(1);

        EnsureThrows<InvalidOperationException>(options.Validate);
    }

    [Test]
    public void QueueDelayBeyondThePortableTimerRangeShouldFailDuringValidation()
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.MaxValue
        };
        options.Global.UseConcurrency(1);

        EnsureThrows<ArgumentOutOfRangeException>(options.Validate);
    }

    [Test]
    public void RatePolicyDurationsBeyondThePortableTimerRangeShouldFailDuringConfiguration()
    {
        new SharpLinkTokenBucketLimitOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = SharpLinkTimer.MaximumDelay
        }.Validate();
        new SharpLinkFixedWindowLimitOptions
        {
            PermitLimit = 1,
            Window = SharpLinkTimer.MaximumDelay
        }.Validate();
        new SharpLinkSlidingWindowLimitOptions
        {
            PermitLimit = 1,
            Window = SharpLinkTimer.MaximumDelay,
            SegmentsPerWindow = 2
        }.Validate();
        var aboveMaximum = SharpLinkTimer.MaximumDelay.Add(TimeSpan.FromTicks(1));
        var failures = new Exception?[]
        {
            CaptureFailure(() => new SharpLinkTokenBucketLimitOptions
            {
                TokenLimit = 1,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = aboveMaximum
            }.Validate()),
            CaptureFailure(() => new SharpLinkFixedWindowLimitOptions
            {
                PermitLimit = 1,
                Window = aboveMaximum
            }.Validate()),
            CaptureFailure(() => new SharpLinkSlidingWindowLimitOptions
            {
                PermitLimit = 1,
                Window = aboveMaximum,
                SegmentsPerWindow = 2
            }.Validate())
        };

        var rejectedCount = 0;
        foreach (var failure in failures)
            if (failure is ArgumentOutOfRangeException)
                rejectedCount++;

        Ensure(rejectedCount == failures.Length,
            $"all timer-backed rate policies must reject the oversized duration; observed " +
            $"{failures[0]?.GetType().Name ?? "none"}, " +
            $"{failures[1]?.GetType().Name ?? "none"}, " +
            $"{failures[2]?.GetType().Name ?? "none"}");
    }

    [Test]
    public void SlidingWindowShouldRejectAZeroTickSegmentDuration()
    {
        new SharpLinkSlidingWindowLimitOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromTicks(2),
            SegmentsPerWindow = 2
        }.Validate();
        var options = new SharpLinkSlidingWindowLimitOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromTicks(1),
            SegmentsPerWindow = 2
        };

        EnsureThrows<ArgumentOutOfRangeException>(options.Validate);
    }

    [Test]
    public void RuleMustRejectMultipleRatePolicies()
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(options => options.PermitLimit = 1);

        EnsureThrows<InvalidOperationException>(() =>
            rule.UseTokenBucket(options =>
            {
                options.TokenLimit = 1;
                options.TokensPerPeriod = 1;
            }));
    }

    [Test]
    public async Task ConcurrencyQueueShouldReleasePermitAndAccountingExactlyOnce()
    {
        var provider = new ManualTimeProvider();
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(2)
        };
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, [], provider);
        var context = CreateContext();

        var first = await controller.AcquireAsync(context, 32, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(context, 48, allowQueue: true, CancellationToken.None);
        Ensure(!pending.IsCompleted, "second call should wait");
        Ensure(controller.ActivePermits == 1, "one active permit");
        Ensure(controller.QueuedCalls == 1 && controller.QueuedBytes == 48, "bounded queue accounting");

        first.Lease!.Dispose();
        var second = await pending.AsTask();
        Ensure(second.IsAcquired, "queued call acquired");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0, "queue accounting released");
        second.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0, "all permits released");
    }

    [Test]
    public async Task ImmediateAdmissionShouldNotAllocateThreeTransientArraysPerCall()
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var context = CreateContext();
        for (var index = 0; index < 2_000; index++)
        {
            var warmup = await controller.AcquireAsync(
                context, 1, allowQueue: false, CancellationToken.None);
            warmup.Lease!.Dispose();
        }

        const int iterations = 20_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            var decision = await controller.AcquireAsync(
                context, 1, allowQueue: false, CancellationToken.None);
            decision.Lease!.Dispose();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var bytesPerCall = allocated / iterations;

        Ensure(bytesPerCall <= 320,
            $"immediate admission allocated {bytesPerCall} B/call after warm-up");
    }

    [Test]
    public async Task PartitionCapacityShouldProtectActiveEntryAndReclaimIdleEntry()
    {
        var key = "first";
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => key,
            partition =>
            {
                partition.MaxPartitions = 1;
                partition.IdleTimeout = TimeSpan.FromMilliseconds(5);
                partition.UseConcurrency(1);
            });
        await using var controller = SharpLinkAdmissionController.Create(options, []);

        var first = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        key = "second";
        var full = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(!full.IsAcquired && full.Reason == "partition_capacity", "active partition cannot be evicted");

        first.Lease!.Dispose();
        await Task.Delay(20);
        var reclaimed = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(reclaimed.IsAcquired && controller.ActivePartitions == 1, "idle partition reclaimed");
        reclaimed.Lease!.Dispose();
    }

    [Test]
    public async Task PartitionRuntimeShouldFreezeConfigurationAtCreation()
    {
        var key = "first";
        SharpLinkPartitionAdmissionOptions? leaked = null;
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => key,
            partition =>
            {
                partition.MaxPartitions = 1;
                partition.UseConcurrency(1);
                leaked = partition;
            });
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        leaked!.MaxPartitions = 2;
        leaked.Concurrency!.PermitLimit = 2;

        var first = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        var samePartition = await controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        key = "second";
        var secondPartition = await controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);

        first.Lease?.Dispose();
        samePartition.Lease?.Dispose();
        secondPartition.Lease?.Dispose();
        Ensure(first.IsAcquired, "first frozen partition permit");
        Ensure(!samePartition.IsAcquired && samePartition.Reason == "concurrency",
            "later concurrency mutation must not alter a new partition runtime");
        Ensure(!secondPartition.IsAcquired && secondPartition.Reason == "partition_capacity",
            "later capacity mutation must not enlarge the live partition pool");
    }

    [Test]
    public async Task EmptyPartitionKeyShouldUseOneExplicitDefaultPartition()
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => null,
            partition => partition.UseConcurrency(1));
        await using var controller = SharpLinkAdmissionController.Create(options, []);

        var first = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        var rejected = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired, "default partition first permit");
        Ensure(!rejected.IsAcquired && rejected.Reason == "concurrency",
            "empty keys share the default partition");
        Ensure(controller.ActivePartitions == 1, "one explicit default partition");

        first.Lease!.Dispose();
        var recovered = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(recovered.IsAcquired, "default partition recovers");
        recovered.Lease!.Dispose();
    }

    [Test]
    public async Task DefaultPartitionShouldNotAliasAUserBusinessKey()
    {
        string? key = null;
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => key,
            partition =>
            {
                partition.MaxPartitions = 2;
                partition.UseConcurrency(1);
            });
        await using var controller = SharpLinkAdmissionController.Create(options, []);

        var defaultLease = await controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        key = string.Empty;
        var sameDefault = await controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        key = "<default>";
        var businessLease = await controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);

        Ensure(defaultLease.IsAcquired, "default partition permit");
        Ensure(!sameDefault.IsAcquired && sameDefault.Reason == "concurrency",
            "null and empty selectors share the default partition");
        Ensure(businessLease.IsAcquired, "sentinel-shaped user key has an independent permit");
        Ensure(controller.ActivePartitions == 2, "default and user partitions are distinct");

        defaultLease.Lease!.Dispose();
        businessLease.Lease!.Dispose();
    }

    [Test]
    [Arguments("token")]
    [Arguments("fixed")]
    [Arguments("sliding")]
    public async Task EveryRatePolicyShouldRejectBeyondItsImmediatePermit(string policy)
    {
        var options = new SharpLinkAdmissionControlOptions();
        switch (policy)
        {
            case "token":
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 1;
                    rate.TokensPerPeriod = 1;
                    rate.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
                });
                break;
            case "fixed":
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 1;
                    rate.Window = TimeSpan.FromMinutes(1);
                });
                break;
            case "sliding":
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 1;
                    rate.Window = TimeSpan.FromMinutes(1);
                    rate.SegmentsPerWindow = 2;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var first = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        var rejected = await controller.AcquireAsync(CreateContext(), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired, $"{policy} first permit");
        Ensure(!rejected.IsAcquired && rejected.Reason == "rate", $"{policy} immediate rejection");
        first.Lease!.Dispose();
    }

    [Test]
    [Arguments("token")]
    [Arguments("fixed")]
    [Arguments("sliding")]
    public async Task CompositeQueueRetryShouldNotConsumeAnUpstreamRatePermitTwice(string policy)
    {
        var provider = new ManualTimeProvider();
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(1)
        };
        switch (policy)
        {
            case "token":
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 2;
                    rate.TokensPerPeriod = 1;
                    rate.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
                });
                break;
            case "fixed":
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = 2;
                    rate.Window = TimeSpan.FromMinutes(1);
                });
                break;
            case "sliding":
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = 2;
                    rate.Window = TimeSpan.FromMinutes(1);
                    rate.SegmentsPerWindow = 2;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
        }
        options.AddContract(1, rule => rule.UseConcurrency(1));
        await using var controller = SharpLinkAdmissionController.Create(options, [], provider);
        var context = CreateContext();

        var first = await controller.AcquireAsync(context, 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(context, 1, allowQueue: true, CancellationToken.None);
        Ensure(!pending.IsCompleted, "downstream concurrency should queue the second request");

        first.Lease!.Dispose();
        var second = await pending.AsTask();

        Ensure(second.IsAcquired, "queued request should reuse its previously consumed rate permit");
        second.Lease!.Dispose();
        var exhausted = await controller.AcquireAsync(
            context,
            1,
            allowQueue: false,
            CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "two logical requests should consume exactly two rate permits");
    }

    [Test]
    public async Task QueuedRateLeaseShouldSurviveAnEarlierLimiterFailure()
    {
        var ownerOptions = new SharpLinkAdmissionControlOptions();
        ownerOptions.Global.UseConcurrency(1);
        await using var owner = SharpLinkAdmissionController.Create(ownerOptions, []);
        using var concurrency = new ScriptedRateLimiter(true, false);
        using var rate = new ScriptedRateLimiter(false);
        using var request = new AdmissionRequest(
            [
                new AdmissionLimiterSlot(concurrency, "global", "concurrency", RetainOnFailure: false),
                new AdmissionLimiterSlot(rate, "contract", "rate", RetainOnFailure: true)
            ],
            slotCount: 2,
            partition: null);

        Ensure(!request.TryAcquire(owner, out _, out var failedRate) &&
            ReferenceEquals(failedRate.Limiter, rate),
            "initial attempt should fail at the downstream rate limiter");
        var queuedRateLease = new ScriptedRateLimitLease(isAcquired: true);
        Ensure(!request.TryAcquireUsing(
                owner,
                rate,
                queuedRateLease,
                out _,
                out var failedConcurrency) &&
            ReferenceEquals(failedConcurrency.Limiter, concurrency),
            "retry should retain its queued rate lease when an earlier limiter fails");

        var queuedConcurrencyLease = new ScriptedRateLimitLease(isAcquired: true);
        Ensure(request.TryAcquireUsing(
                owner,
                concurrency,
                queuedConcurrencyLease,
                out var admissionLease,
                out _),
            "later retry should reuse the retained downstream rate lease");
        Ensure(rate.AttemptCount == 1, "logical request should attempt the rate limiter only once");

        admissionLease!.Dispose();
        Ensure(queuedRateLease.DisposeCount == 1, "retained rate lease transferred and disposed once");
    }

    [Test]
    public async Task PermitCancellationRacesShouldLeaveAllAccountingAtZero()
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(2)
        };
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var context = CreateContext();

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var first = await controller.AcquireAsync(context, 1, true, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var pending = controller.AcquireAsync(context, 1, true, cancellation.Token);
            var cancel = Task.Run(cancellation.Cancel);
            var release = Task.Run(first.Lease!.Dispose);
            await Task.WhenAll(cancel, release);
            try
            {
                var decision = await pending;
                decision.Lease?.Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }

        Ensure(controller.ActivePermits == 0, "race active permits");
        Ensure(controller.QueuedCalls == 0, "race queued calls");
        Ensure(controller.QueuedBytes == 0, "race queued bytes");
    }

    [Test]
    public async Task DeadlineThatLimitsQueueWaitShouldReturnDeadlineExceeded()
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(2)
        };
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var first = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        var deadlineContext = new SharpLinkAdmissionContext(
            1,
            2,
            RpcMethodKind.Unary,
            "connection",
            authenticationContext: null,
            metadata: null);
        var deadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(50), TimeProvider.System);

        var rejected = await controller.AcquireAsync(
            deadlineContext,
            retainedBytes: 1,
            allowQueue: true,
            deadline: deadline,
            cancellationToken: CancellationToken.None);

        Ensure(!rejected.IsAcquired, "deadline-limited call should be rejected");
        Ensure(rejected.ErrorCode == SharpLinkErrorCode.DeadlineExceeded,
            "deadline-limited call error code");
        Ensure(rejected.Reason == "deadline", "deadline-limited call reason");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0,
            "deadline queue accounting released");
        first.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0, "deadline active permit released");
    }

    [Test]
    public async Task AdmissionDeadlineShouldRejectAtExactFakeEqualityAndReleaseEveryQueueCounter()
    {
        var provider = new ManualTimeProvider();
        var options = QueuedConcurrencyOptions(TimeSpan.FromSeconds(10));
        await using var controller = SharpLinkAdmissionController.Create(options, [], provider);
        var first = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), provider);
        var pending = controller.AcquireAsync(
            CreateContext(),
            retainedBytes: 64,
            allowQueue: true,
            deadline: deadline,
            cancellationToken: CancellationToken.None).AsTask();

        Ensure(!pending.IsCompleted && controller.QueuedCalls == 1 &&
               controller.QueuedBytes == 64 && controller.ActivePermits == 1,
            "the deadline-limited request must hold exactly one bounded queue reservation");
        Ensure(provider.ActiveTimerCount == 1,
            "the queued deadline must own one provider timer");

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!pending.IsCompleted && controller.QueuedCalls == 1,
            "one provider tick before the deadline must remain queued");

        provider.Advance(TimeSpan.FromTicks(1));
        var rejected = await pending;
        Ensure(!rejected.IsAcquired &&
               rejected.ErrorCode == SharpLinkErrorCode.DeadlineExceeded &&
               rejected.Reason == "deadline",
            "exact monotonic deadline equality must produce the stable deadline result");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0 &&
               controller.ActivePermits == 1,
            "the timeout winner must release queue accounting without stealing the held permit");
        Ensure(provider.ActiveTimerCount == 0,
            "the terminal admission result must dispose its provider timer");

        provider.Advance(TimeSpan.FromHours(1));
        Ensure(pending.IsCompletedSuccessfully && !pending.Result.IsAcquired,
            "advancing after the deadline must not resurrect the rejected waiter");
        first.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0,
            "the independently held permit must remain releasable after the timeout");
    }

    [Test]
    public async Task AdmissionMaxQueueDelayShouldRejectAtExactFakeEqualityWithoutAGhostPermit()
    {
        var provider = new ManualTimeProvider();
        var options = QueuedConcurrencyOptions(TimeSpan.FromSeconds(5));
        await using var controller = SharpLinkAdmissionController.Create(options, [], provider);
        var first = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(
            CreateContext(), 32, allowQueue: true, CancellationToken.None).AsTask();

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!pending.IsCompleted && controller.QueuedCalls == 1,
            "the maximum queue delay must remain live immediately before equality");

        provider.Advance(TimeSpan.FromTicks(1));
        var rejected = await pending;
        Ensure(!rejected.IsAcquired &&
               rejected.ErrorCode == SharpLinkErrorCode.ResourceExhausted &&
               rejected.Reason == "concurrency",
            "max queue equality without a call deadline must preserve admission rejection semantics");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0 &&
               controller.ActivePermits == 1 && provider.ActiveTimerCount == 0,
            "queue timeout must release its reservation and timer without acquiring a ghost permit");

        first.Lease!.Dispose();
        var recovered = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(recovered.IsAcquired,
            "the permit must remain usable by the next request after a queue timeout");
        recovered.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0,
            "the recovered request must leave permit accounting balanced");
    }

    [Test]
    public async Task AdmissionPartitionShouldReclaimAtExactProviderIdleEquality()
    {
        var provider = new ManualTimeProvider();
        var key = "first";
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(
            _ => key,
            partition =>
            {
                partition.MaxPartitions = 1;
                partition.IdleTimeout = TimeSpan.FromSeconds(5);
                partition.UseConcurrency(1);
            });
        await using var controller = SharpLinkAdmissionController.Create(options, [], provider);
        provider.Advance(TimeSpan.FromTicks(1));
        var first = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        first.Lease!.Dispose();
        key = "second";

        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        var before = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(!before.IsAcquired && before.Reason == "partition_capacity" &&
               controller.ActivePartitions == 1,
            "one provider tick before idle expiry must retain the original partition");

        provider.Advance(TimeSpan.FromTicks(1));
        var atEquality = await controller.AcquireAsync(
            CreateContext(), 1, allowQueue: false, CancellationToken.None);
        Ensure(atEquality.IsAcquired && controller.ActivePartitions == 1,
            "exact IdleTimeout equality must reclaim the old entry and admit the new partition");
        atEquality.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0 && provider.ActiveTimerCount == 0,
            "partition reclamation must balance its permit and must not create timers");
    }

    [Test]
    public async Task AdmissionControllersWithDifferentProvidersShouldAdvanceIndependently()
    {
        var firstProvider = new ManualTimeProvider();
        var secondProvider = new ManualTimeProvider();
        var firstOptions = QueuedConcurrencyOptions(TimeSpan.FromSeconds(5));
        var secondOptions = QueuedConcurrencyOptions(TimeSpan.FromSeconds(5));
        await using var firstController = SharpLinkAdmissionController.Create(
            firstOptions, [], firstProvider);
        await using var secondController = SharpLinkAdmissionController.Create(
            secondOptions, [], secondProvider);
        var firstOwner = await firstController.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        var secondOwner = await secondController.AcquireAsync(
            CreateContext(), 1, allowQueue: true, CancellationToken.None);
        var firstPending = firstController.AcquireAsync(
            CreateContext(), 16, allowQueue: true, CancellationToken.None).AsTask();
        var secondPending = secondController.AcquireAsync(
            CreateContext(), 24, allowQueue: true, CancellationToken.None).AsTask();

        firstProvider.Advance(TimeSpan.FromSeconds(5));
        var firstRejected = await firstPending;
        Ensure(!firstRejected.IsAcquired &&
               firstController.QueuedCalls == 0 &&
               firstProvider.ActiveTimerCount == 0,
            "advancing the first provider must expire only its queued admission");
        Ensure(!secondPending.IsCompleted &&
               secondController.QueuedCalls == 1 &&
               secondController.QueuedBytes == 24 &&
               secondProvider.ActiveTimerCount == 1,
            "the second controller must remain queued on its independent provider");

        secondOwner.Lease!.Dispose();
        var secondAdmitted = await secondPending;
        Ensure(secondAdmitted.IsAcquired &&
               secondController.QueuedCalls == 0 &&
               secondProvider.ActiveTimerCount == 0,
            "releasing the second controller permit must complete normally without advancing time");
        secondAdmitted.Lease!.Dispose();
        firstOwner.Lease!.Dispose();
        Ensure(firstController.ActivePermits == 0 && secondController.ActivePermits == 0,
            "both independent permit domains must return to zero");
    }

    private static SharpLinkAdmissionControlOptions QueuedConcurrencyOptions(TimeSpan maxQueueDelay)
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = maxQueueDelay
        };
        options.Global.UseConcurrency(1);
        return options;
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(1, 2, RpcMethodKind.Unary, "connection", null, null);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
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

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"Expected {typeof(TException).Name}.");
        }
        catch (TException)
        {
        }
    }

    private sealed class ScriptedRateLimiter(params bool[] acquisitionResults) : RateLimiter
    {
        private readonly Queue<bool> _acquisitionResults = new(acquisitionResults);

        internal int AttemptCount { get; private set; }
        public override TimeSpan? IdleDuration => null;
        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            _ = permitCount;
            AttemptCount++;
            return new ScriptedRateLimitLease(
                _acquisitionResults.Count != 0 && _acquisitionResults.Dequeue());
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(AttemptAcquireCore(permitCount));
        }
    }

    private sealed class ScriptedRateLimitLease(bool isAcquired) : RateLimitLease
    {
        internal int DisposeCount { get; private set; }
        public override bool IsAcquired { get; } = isAcquired;
        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            _ = metadataName;
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            _ = disposing;
            DisposeCount++;
        }
    }
}
