using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using SharpLink.Server;

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
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(2)
        };
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var context = CreateContext();

        var first = await controller.AcquireAsync(context, 32, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(context, 48, allowQueue: true, CancellationToken.None);
        Ensure(!pending.IsCompleted, "second call should wait");
        Ensure(controller.ActivePermits == 1, "one active permit");
        Ensure(controller.QueuedCalls == 1 && controller.QueuedBytes == 48, "bounded queue accounting");

        first.Lease!.Dispose();
        var second = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(second.IsAcquired, "queued call acquired");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0, "queue accounting released");
        second.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0, "all permits released");
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
        await using var controller = SharpLinkAdmissionController.Create(options, []);
        var context = CreateContext();

        var first = await controller.AcquireAsync(context, 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(context, 1, allowQueue: true, CancellationToken.None);
        Ensure(!pending.IsCompleted, "downstream concurrency should queue the second request");

        first.Lease!.Dispose();
        var second = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(2));

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
            metadata: null,
            DateTimeOffset.UtcNow.AddMilliseconds(50));

        var rejected = await controller.AcquireAsync(
            deadlineContext, 1, allowQueue: true, CancellationToken.None);

        Ensure(!rejected.IsAcquired, "deadline-limited call should be rejected");
        Ensure(rejected.ErrorCode == SharpLinkErrorCode.DeadlineExceeded,
            "deadline-limited call error code");
        Ensure(rejected.Reason == "deadline", "deadline-limited call reason");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0,
            "deadline queue accounting released");
        first.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0, "deadline active permit released");
    }

    private static SharpLinkAdmissionContext CreateContext()
        => new(1, 2, RpcMethodKind.Unary, "connection", null, null, null);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
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
