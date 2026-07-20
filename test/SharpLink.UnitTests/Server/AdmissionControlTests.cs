using System.Threading;
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
}
