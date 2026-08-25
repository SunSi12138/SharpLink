using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicUpdateTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> TenantSelector =
        static _ => "tenant-a";
    private static readonly Func<SharpLinkAdmissionContext, string?> OtherTenantSelector =
        static _ => "tenant-b";

    [Test]
    public async Task ConcurrencyIncreaseShouldReuseStateAndWakeOldestQueuedRequest()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 1, 2, 1024));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var kernel = source.Kernel;
        var context = CreateContext();

        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var firstQueued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        var secondQueued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => kernel.QueuedCalls == 2 && state.WaitingCount == 2,
            "both requests must own an outer queue reservation before entering the concurrency waiter");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 2, 2, 1024));
        var replacement = Current(server);
        Ensure(ReferenceEquals(state, replacement.Controller.GlobalConcurrencyStateForTests),
            "concurrency resize must preserve logical-scope state identity");
        Ensure(state.PermitLimit == 2 && state.ActiveCount == 2,
            "increase must expose only the newly available capacity");

        var first = await firstQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(first.IsAcquired, "oldest queued request must wake promptly on capacity increase");
        Ensure(!secondQueued.IsCompleted,
            "increase from one to two with one holder may wake exactly one queued request");

        holder.Lease!.Dispose();
        var second = await secondQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(second.IsAcquired, "second waiter must follow FIFO after one permit is released");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && kernel.ActivePermits == 0 &&
               state.WaitingCount == 0 && state.ActiveCount == 0,
            "increase path must drain all queue and permit accounting");
    }

    [Test]
    public async Task ConcurrencyShrinkShouldKeepExistingHoldersAndQueuedWaiter()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 3, 2, 1024));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var context = CreateContext();

        var first = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var third = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => state.WaitingCount == 1, "fourth request must be queued before shrink");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 2, 1024));
        var replacement = Current(server);
        Ensure(ReferenceEquals(state, replacement.Controller.GlobalConcurrencyStateForTests) &&
               state.PermitLimit == 1 && state.ActiveCount == 3,
            "shrink must change only the target and keep all three existing holders");
        Ensure(!(await replacement.Controller.AcquireAsync(
                context, 1, allowQueue: false, CancellationToken.None)).IsAcquired,
            "no new request may enter while active count remains above the shrunken target");

        first.Lease!.Dispose();
        Ensure(state.ActiveCount == 2 && !queued.IsCompleted,
            "first release above target must not cancel or wake the queued waiter");
        second.Lease!.Dispose();
        Ensure(state.ActiveCount == 1 && !queued.IsCompleted,
            "active equal to target still leaves no free capacity");
        third.Lease!.Dispose();

        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired && state.ActiveCount == 1,
            "queued request must survive shrink and enter after natural releases reach capacity");
        admitted.Lease!.Dispose();
        Ensure(state.ActiveCount == 0 && state.WaitingCount == 0 &&
               replacement.Controller.ActivePermits == 0 && replacement.Controller.QueuedCalls == 0,
            "shrink path must finish without permit underflow or stranded waiter");
    }

    [Test]
    public async Task ContractAndMethodConcurrencyShouldResizeRemoveAndAddIndependently()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureComposite(options, 8, 2, 1));
        var source = Current(server);
        var global = source.Controller.GlobalConcurrencyStateForTests!;
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        var method = source.Controller.MethodConcurrencyStateForTests(101, 202)!;

        publicServer.UpdateAdmissionControl(options => ConfigureComposite(options, 9, 3, 2));
        var resized = Current(server);
        Ensure(ReferenceEquals(global, resized.Controller.GlobalConcurrencyStateForTests) &&
               ReferenceEquals(contract, resized.Controller.ContractConcurrencyStateForTests(101)) &&
               ReferenceEquals(method, resized.Controller.MethodConcurrencyStateForTests(101, 202)),
            "Global, Contract, and Method resize must preserve each logical-scope state identity");
        Ensure(global.PermitLimit == 9 && contract.PermitLimit == 3 && method.PermitLimit == 2,
            "all three mutable concurrency targets must commit together");

        Ensure(resized.TryAcquireUse(), "test must retain the pre-removal generation");
        publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(9));
        var removed = Current(server);
        Ensure(removed.Controller.ContractConcurrencyStateForTests(101) is null &&
               removed.Controller.MethodConcurrencyStateForTests(101, 202) is null,
            "complete candidate omission must remove Contract and Method concurrency from N+1");
        Ensure(resized.IsRetired && !resized.IsReclaimed,
            "old captured generation must remain alive while its use is retained");
        Ensure(source.Kernel.ConcurrencyStateCount == 3,
            "removed component states must remain registered while the old generation is captured");
        resized.ReleaseUse();
        Ensure(resized.IsReclaimed && source.Kernel.ConcurrencyStateCount == 1,
            "removed component states must reclaim after the final old-generation use ends");

        publicServer.UpdateAdmissionControl(options => ConfigureComposite(options, 9, 4, 2));
        var added = Current(server);
        var newContract = added.Controller.ContractConcurrencyStateForTests(101)!;
        var newMethod = added.Controller.MethodConcurrencyStateForTests(101, 202)!;
        Ensure(!ReferenceEquals(contract, newContract) && !ReferenceEquals(method, newMethod),
            "adding previously removed concurrency must create new component state");

        var context = CreateContext();
        var first = await added.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var second = await added.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        var third = await added.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired && !third.IsAcquired && third.Reason == "concurrency",
            "new Method limit two must participate in the composed Global/Contract/Method admission path");
        first.Lease!.Dispose();
        second.Lease!.Dispose();
    }

    [Test]
    [Arguments(RateKind.TokenBucket)]
    [Arguments(RateKind.FixedWindow)]
    [Arguments(RateKind.SlidingWindow)]
    public async Task UnchangedRateStateShouldBePreservedAcrossConcurrencyUpdate(RateKind kind)
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureRate(options, kind, concurrency: 1));
        var source = Current(server);
        var rate = source.Controller.GlobalRateStateForTests!;
        var context = CreateContext();

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "first request must consume the single rate permit");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => ConfigureRate(options, kind, concurrency: 2));
        var replacement = Current(server);
        Ensure(ReferenceEquals(rate, replacement.Controller.GlobalRateStateForTests),
            $"{kind}: unchanged rate state must be reused exactly");
        Ensure(replacement.Controller.GlobalConcurrencyStateForTests!.PermitLimit == 2,
            $"{kind}: concurrency update must still commit");
        var exhausted = await replacement.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            $"{kind}: consumed rate quota must not reset across update");
    }

    [Test]
    public async Task RetainedRateLeaseShouldSurviveQueuePolicyUpdate()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueueBounds(options, 2, 1024, TimeSpan.FromMinutes(1));
            options.Global.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 1;
                rate.TokensPerPeriod = 1;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
            options.AddContract(101, rule => rule.UseConcurrency(1));
        });
        var source = Current(server);
        var rate = source.Controller.GlobalRateStateForTests!;
        var contract = source.Controller.ContractConcurrencyStateForTests(101)!;
        using var blocker = contract.AttemptAcquire(1);
        Ensure(blocker.IsAcquired, "test must occupy downstream contract concurrency without consuming global rate");

        var queued = source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => source.Kernel.QueuedCalls == 1 && contract.WaitingCount == 1,
            "request must retain its global rate lease while waiting on downstream concurrency");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureQueueBounds(options, 4, 2048, TimeSpan.FromMinutes(2));
            options.Global.UseTokenBucket(value =>
            {
                value.TokenLimit = 1;
                value.TokensPerPeriod = 1;
                value.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
            options.AddContract(101, rule => rule.UseConcurrency(2));
        });
        var replacement = Current(server);
        Ensure(ReferenceEquals(rate, replacement.Controller.GlobalRateStateForTests),
            "queue/concurrency update must preserve the rate state holding the queued request's lease");
        var newAttempt = await replacement.Controller.AcquireAsync(
            CreateContext(), 1, false, CancellationToken.None);
        Ensure(!newAttempt.IsAcquired && newAttempt.Reason == "rate",
            "retained old-generation rate lease must continue to consume shared rate quota");

        blocker.Dispose();
        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired,
            "old queued request must reuse its retained rate lease after the update");
        admitted.Lease!.Dispose();
    }

    [Test]
    public async Task QueueCountAndBytesUpdatesShouldNotEvictExistingWaiters()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 1, 3, 20));
        var source = Current(server);
        var kernel = source.Kernel;
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var context = CreateContext();
        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var first = source.Controller.AcquireAsync(context, 12, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => kernel.QueuedCalls == 1 && kernel.QueuedBytes == 12,
            "first request must be resident before queue-bound shrink");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 3, 8));
        Ensure(kernel.QueuedCalls == 1 && kernel.QueuedBytes == 12 && !first.IsCompleted,
            "byte shrink must not evict or cancel an existing queued request");
        var byteRejected = await Current(server).Controller.AcquireAsync(
            context, 1, true, CancellationToken.None);
        Ensure(!byteRejected.IsAcquired && byteRejected.Reason == "queue_bytes",
            "new N+1 request must apply the smaller byte bound against shared current residency");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 3, 30));
        var second = Current(server).Controller.AcquireAsync(context, 8, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => kernel.QueuedCalls == 2 && kernel.QueuedBytes == 20,
            "byte increase must admit new residency without recreating the queue domain");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 1, 30));
        Ensure(kernel.QueuedCalls == 2 && !first.IsCompleted && !second.IsCompleted,
            "count shrink must preserve both already queued requests");
        var countRejected = await Current(server).Controller.AcquireAsync(
            context, 1, true, CancellationToken.None);
        Ensure(!countRejected.IsAcquired && countRejected.Reason == "queue_count",
            "new N+1 request must apply the smaller count bound against shared current residency");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 3, 30));
        var third = Current(server).Controller.AcquireAsync(context, 5, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => kernel.QueuedCalls == 3 && kernel.QueuedBytes == 25,
            "count increase must admit another request into the same shared queue accounting domain");
        Ensure(state.WaitingCount == kernel.QueuedCalls,
            "every underlying concurrency waiter must correspond to exactly one outer queue reservation");

        holder.Lease!.Dispose();
        foreach (var pending in new[] { first, second, third })
        {
            var decision = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(decision.IsAcquired, "resident waiter must survive queue-policy updates");
            decision.Lease!.Dispose();
        }
        Ensure(kernel.QueuedCalls == 0 && kernel.QueuedBytes == 0 && state.WaitingCount == 0,
            "queue-policy update sequence must fully drain shared accounting and internal waiters");
    }

    [Test]
    public async Task QueuedRequestShouldKeepCapturedMaxQueueDelay()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigureQueue(options, 1, 2, 1024, TimeSpan.FromMinutes(1)));
        var source = Current(server);
        var context = CreateContext();
        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        using var oldCancellation = new CancellationTokenSource();
        var oldQueued = source.Controller.AcquireAsync(
            context, 1, true, oldCancellation.Token).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "old request must enter queue before delay update");

        publicServer.UpdateAdmissionControl(options =>
            ConfigureQueue(options, 1, 2, 1024, TimeSpan.FromMilliseconds(50)));
        var replacement = Current(server);
        Ensure(source.Controller.MaxQueueDelayForTests == TimeSpan.FromMinutes(1) &&
               replacement.Controller.MaxQueueDelayForTests == TimeSpan.FromMilliseconds(50),
            "program generations must keep immutable queue-delay snapshots");
        var newQueued = replacement.Controller.AcquireAsync(
            context, 1, true, CancellationToken.None).AsTask();
        var newDecision = await newQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!newDecision.IsAcquired, "new N+1 waiter must use the shorter queue delay");
        Ensure(!oldQueued.IsCompleted,
            "already queued N request must not have its captured longer delay retroactively shortened");

        oldCancellation.Cancel();
        Ensure(await CaptureAsyncFailure(oldQueued) is OperationCanceledException,
            "test cancellation must terminate the old long-delay waiter without changing its policy result");
        holder.Lease!.Dispose();
        Ensure(replacement.Controller.QueuedCalls == 0 && replacement.Controller.QueuedBytes == 0,
            "delay update and cancellation must release queue accounting exactly once");
    }

    [Test]
    public async Task QueueOneWayCallsUpdateShouldBeNextRequestScoped()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options, 1, 3, 1024);
            options.QueueOneWayCalls = true;
        });
        var source = Current(server);
        var oneWay = CreateContext(RpcMethodKind.OneWay);
        var unary = CreateContext(RpcMethodKind.Unary);
        var holder = await source.Controller.AcquireAsync(unary, 1, true, CancellationToken.None);
        var oldQueued = source.Controller.AcquireAsync(
            oneWay, 1, source.Controller.QueueOneWayCalls, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "old OneWay request must already be queued under the true snapshot");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 1, 3, 1024));
        var disabledQueue = Current(server);
        Ensure(!disabledQueue.Controller.QueueOneWayCalls && source.Controller.QueueOneWayCalls,
            "QueueOneWayCalls must be immutable per captured program generation");
        var newOneWay = await disabledQueue.Controller.AcquireAsync(
            oneWay, 1, disabledQueue.Controller.QueueOneWayCalls, CancellationToken.None);
        Ensure(!newOneWay.IsAcquired && disabledQueue.Controller.QueuedCalls == 1,
            "new OneWay request must reject immediately while the old queued OneWay remains resident");
        var twoWay = disabledQueue.Controller.AcquireAsync(unary, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => disabledQueue.Controller.QueuedCalls == 2,
            "two-way queuing must remain enabled independently of QueueOneWayCalls");

        holder.Lease!.Dispose();
        var oldAdmitted = await oldQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(oldAdmitted.IsAcquired, "old queued OneWay request must survive the false update");
        oldAdmitted.Lease!.Dispose();
        var unaryAdmitted = await twoWay.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(unaryAdmitted.IsAcquired, "two-way waiter must remain unaffected");
        unaryAdmitted.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureQueue(options, 1, 3, 1024);
            options.QueueOneWayCalls = true;
        });
        var enabledQueue = Current(server);
        var blocker = await enabledQueue.Controller.AcquireAsync(unary, 1, true, CancellationToken.None);
        var newQueued = enabledQueue.Controller.AcquireAsync(
            oneWay, 1, enabledQueue.Controller.QueueOneWayCalls, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => enabledQueue.Controller.QueuedCalls == 1,
            "new OneWay request must queue after false-to-true update");
        blocker.Lease!.Dispose();
        var admitted = await newQueued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired, "new OneWay waiter must complete after capacity returns");
        admitted.Lease!.Dispose();
    }

    [Test]
    public async Task PartitionPoolAndConsumedQuotaShouldSurviveNonPartitionUpdate()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigurePartition(options, globalConcurrency: 2));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = CreateContext();
        var first = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && pool.Count == 1, "first request must create the partition and consume its token");
        first.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigurePartition(options, globalConcurrency: 3);
            ConfigureQueueBounds(options, 2, 1024, TimeSpan.FromSeconds(5));
        });
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests) && pool.Count == 1,
            "non-partition concurrency/queue update must reuse the exact partition pool and live entries");
        var second = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(!second.IsAcquired && second.Reason == "rate" && second.Scope == "partition",
            "consumed partition rate quota must not reset across a non-partition update");

        var before = replacement;
        publicServer.UpdateAdmissionControl(options =>
        {
            options.Global.UseConcurrency(3);
            ConfigureQueueBounds(options, 2, 1024, TimeSpan.FromSeconds(5));
            options.UsePartition(TenantSelector, partition =>
            {
                partition.MaxPartitions = 9;
                partition.IdleTimeout = TimeSpan.FromHours(1);
                partition.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = 1;
                    rate.TokensPerPeriod = 1;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                });
            });
        });
        var partitionUpdated = Current(server);
        Ensure(!ReferenceEquals(before, partitionUpdated) &&
               ReferenceEquals(pool, partitionUpdated.Controller.PartitionStateForTests) &&
               pool.MaxPartitionsForTests == 9,
            "same-selector partition policy update must publish while preserving the authoritative pool");
        var stillExhausted = await partitionUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!stillExhausted.IsAcquired && stillExhausted.Reason == "rate" &&
               stillExhausted.Scope == "partition",
            "MaxPartitions update must not reset consumed partition quota");
    }

    [Test]
    public async Task RateTransitionsShouldSucceedWhilePartitionTransitionsRemainTransactional()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 10, rateLimit: 1));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var context = CreateContext();

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "source rate permit must be consumed before the public update path is exercised");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 5, rateLimit: 2));
        var parameterUpdated = Current(server);
        Ensure(!ReferenceEquals(source, parameterUpdated) &&
               ReferenceEquals(state, parameterUpdated.Controller.GlobalConcurrencyStateForTests) &&
               state.PermitLimit == 5,
            "rate parameter change must publish while preserving the logical concurrency state");
        var additional = await parameterUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(additional.IsAcquired,
            "raising the rate limit from one to two after one consumed permit may expose exactly one additional permit");
        additional.Lease!.Dispose();
        var exhausted = await parameterUpdated.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "rate parameter update must not expose a fresh full quota");

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.FixedWindow, concurrency: 5, rateLimit: 1));
        var replaced = Current(server);
        var replacementAttempt = await replaced.Controller.AcquireAsync(
            context, 1, false, CancellationToken.None);
        Ensure(!replacementAttempt.IsAcquired && replacementAttempt.Reason == "rate",
            "algorithm replacement must carry a conservative debt barrier into the target algorithm");

        publicServer.UpdateAdmissionControl(options => options.Global.UseConcurrency(5));
        var removed = Current(server);
        Ensure(removed.Controller.GlobalRateStateForTests is null &&
               ReferenceEquals(state, removed.Controller.GlobalConcurrencyStateForTests),
            "rate removal must publish without replacing the unchanged concurrency state");

        publicServer.UpdateAdmissionControl(options => ConfigureRate(
            options, RateKind.TokenBucket, concurrency: 5, rateLimit: 1));
        var readded = Current(server);
        Ensure(readded.Controller.GlobalRateStateForTests is not null &&
               ReferenceEquals(state, readded.Controller.GlobalConcurrencyStateForTests),
            "rate addition after removal must publish a fresh current component while preserving concurrency");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureRate(options, RateKind.TokenBucket, concurrency: 5, rateLimit: 1);
            options.UsePartition(TenantSelector, partition => partition.UseConcurrency(1));
        });
        var partitionAdded = Current(server);
        Ensure(partitionAdded.Controller.PartitionStateForTests is not null &&
               ReferenceEquals(state, partitionAdded.Controller.GlobalConcurrencyStateForTests),
            "partition addition must publish independently while preserving unchanged non-partition state");
    }

    [Test]
    [NotInParallel]
    public async Task LosingConcurrentUpdateMustNotCommitItsResizePlan()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 10, 10, 4096));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var kernel = source.Kernel;
        using var loserAtWriter = new ManualResetEventSlim();
        using var releaseLoser = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate?.Controller.MaxQueuedCallsForTests != 5)
                    return;
                loserAtWriter.Set();
                if (!releaseLoser.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("losing update barrier timed out");
            };

            var loserTask = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => ConfigureQueue(options, 5, 5, 4096))));
            Ensure(loserAtWriter.Wait(TimeSpan.FromSeconds(5)),
                "candidate A must reach the deterministic pre-writer barrier");

            var winnerFailure = CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => ConfigureQueue(options, 20, 20, 4096)));
            Ensure(winnerFailure is null && state.PermitLimit == 20,
                "candidate B must win and commit target 20 while A remains speculative");
            releaseLoser.Set();
            var loserFailure = await loserTask.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(loserFailure is InvalidOperationException && state.PermitLimit == 20,
                "stale candidate A must fail expected-source validation and must never commit target 5");
            Ensure(Current(server).Controller.MaxQueuedCallsForTests == 20 &&
                   kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
                   kernel.ConcurrencyStateCount == 1,
                "losing candidate bindings and reconcile plan must be fully reclaimed");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            releaseLoser.Set();
        }
    }

    [Test]
    [NotInParallel]
    public async Task UpdateLosingToDisableAndReenableMustNotMutateEitherGeneration()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 10, 10, 4096));
        var source = Current(server);
        var oldState = source.Controller.GlobalConcurrencyStateForTests!;
        using var updateAtWriter = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate?.Controller.MaxQueuedCallsForTests != 5)
                    return;
                updateAtWriter.Set();
                if (!releaseUpdate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("disable/reenable update barrier timed out");
            };

            var updateTask = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => ConfigureQueue(options, 5, 5, 4096))));
            Ensure(updateAtWriter.Wait(TimeSpan.FromSeconds(5)),
                "update must finish candidate construction before the disabled-boundary writers run");
            publicServer.DisableAdmissionControl();
            publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 30, 30, 4096));
            var replacement = Current(server);
            var replacementState = replacement.Controller.GlobalConcurrencyStateForTests!;
            Ensure(!ReferenceEquals(oldState, replacementState) && replacementState.PermitLimit == 30,
                "reenable across disabled boundary must publish its own stable state");

            releaseUpdate.Set();
            var failure = await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure is InvalidOperationException && oldState.PermitLimit == 10 &&
                   ReferenceEquals(replacement, Current(server)) && replacementState.PermitLimit == 30,
                "stale update must neither commit target 5 nor overwrite the reenabled generation");
            Ensure(replacement.Kernel.LiveProgramCount == 1 && replacement.Kernel.ConcurrencyStateCount == 1,
                "stale disabled-boundary candidate and retired source state must fully reclaim");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            releaseUpdate.Set();
        }
    }

    [Test]
    [NotInParallel]
    public async Task UpdateLosingToStopShouldRejectAndDrainWithoutLiveMutation()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 10, 10, 4096));
        var source = Current(server);
        var state = source.Controller.GlobalConcurrencyStateForTests!;
        var kernel = source.Kernel;
        using var updateAtWriter = new ManualResetEventSlim();
        using var releaseUpdate = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate?.Controller.MaxQueuedCallsForTests != 5)
                    return;
                updateAtWriter.Set();
                if (!releaseUpdate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Stop update barrier timed out");
            };

            var updateTask = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(
                options => ConfigureQueue(options, 5, 5, 4096))));
            Ensure(updateAtWriter.Wait(TimeSpan.FromSeconds(5)),
                "update must reach deterministic pre-writer barrier");
            var stopTask = server.StopAsync(TimeSpan.Zero).AsTask();
            await WaitUntilAsync(() => kernel.IsDraining, "Stop must seal the kernel before update resumes");
            Ensure(state.PermitLimit == 10,
                "speculative update must not resize live state before it wins publication");
            releaseUpdate.Set();

            var failure = await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure is InvalidOperationException,
                "update linearized after Stop seal must reject predictably");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(kernel.LiveProgramCount == 0 && kernel.RetiredProgramCount == 0 &&
                   kernel.ConcurrencyStateCount == 0 && kernel.RateStateCount == 0 &&
                   kernel.PartitionStateCount == 0 && kernel.QueuedCalls == 0 &&
                   kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
                "Stop must drain current, retired, and losing-candidate admission state exactly once");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            releaseUpdate.Set();
        }
    }

    [Test]
    public async Task OldCapturedGenerationShouldFinishAndRepeatedUpdatesShouldStayBounded()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => ConfigureQueue(options, 1, 2, 1024));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must hold the exact old generation as an active Request would");
        var oldLease = await source.Controller.AcquireAsync(
            CreateContext(), 1, true, CancellationToken.None);
        Ensure(oldLease.IsAcquired, "old generation request must hold its permit before update");

        publicServer.UpdateAdmissionControl(options => ConfigureQueue(options, 2, 3, 2048));
        Ensure(source.IsRetired && !source.IsReclaimed && source.ActiveUses == 1,
            "published N+1 must retire but not reclaim an actively captured N");
        oldLease.Lease!.Dispose();
        source.ReleaseUse();
        Ensure(source.IsReclaimed && source.ReclaimCount == 1,
            "old captured Request completion must allow exactly-once generation reclamation");

        var stableState = Current(server).Controller.GlobalConcurrencyStateForTests!;
        for (var index = 0; index < 64; index++)
        {
            var permitLimit = index % 2 == 0 ? 1 : 2;
            var queuedCalls = 2 + index % 3;
            publicServer.UpdateAdmissionControl(options =>
                ConfigureQueue(options, permitLimit, queuedCalls, 2048 + index));
            var current = Current(server);
            Ensure(ReferenceEquals(stableState, current.Controller.GlobalConcurrencyStateForTests),
                "repeated resize must retain one stable concurrency component");
        }

        var kernel = Current(server).Kernel;
        Ensure(kernel.LiveProgramCount == 1 && kernel.RetiredProgramCount == 0 &&
               kernel.ConcurrencyStateCount == 1 && kernel.RateStateCount == 0 &&
               kernel.PartitionStateCount == 0 && kernel.QueuedCalls == 0 &&
               kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
            "repeated concurrency and queue-policy updates must keep registries and accounting bounded");
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext CreateContext(RpcMethodKind kind = RpcMethodKind.Unary)
        => new(101, 202, kind, "dynamic-update-test", null, null, null);

    private static void ConfigureComposite(
        SharpLinkAdmissionControlOptions options,
        int global,
        int contract,
        int method)
    {
        options.Global.UseConcurrency(global);
        options.AddContract(101, rule => rule.UseConcurrency(contract));
        options.AddMethod(101, 202, rule => rule.UseConcurrency(method));
    }

    private static void ConfigureQueue(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        int maxQueuedCalls,
        long maxQueuedBytes,
        TimeSpan? maxQueueDelay = null)
    {
        options.Global.UseConcurrency(permitLimit);
        ConfigureQueueBounds(
            options,
            maxQueuedCalls,
            maxQueuedBytes,
            maxQueueDelay ?? TimeSpan.FromMinutes(1));
    }

    private static void ConfigureQueueBounds(
        SharpLinkAdmissionControlOptions options,
        int maxQueuedCalls,
        long maxQueuedBytes,
        TimeSpan maxQueueDelay)
    {
        options.MaxQueuedCalls = maxQueuedCalls;
        options.MaxQueuedBytes = maxQueuedBytes;
        options.MaxQueueDelay = maxQueueDelay;
    }

    private static void ConfigureRate(
        SharpLinkAdmissionControlOptions options,
        RateKind kind,
        int concurrency,
        int rateLimit = 1)
    {
        options.Global.UseConcurrency(concurrency);
        switch (kind)
        {
            case RateKind.TokenBucket:
                options.Global.UseTokenBucket(rate =>
                {
                    rate.TokenLimit = rateLimit;
                    rate.TokensPerPeriod = rateLimit;
                    rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.FixedWindow:
                options.Global.UseFixedWindow(rate =>
                {
                    rate.PermitLimit = rateLimit;
                    rate.Window = TimeSpan.FromHours(1);
                });
                break;
            case RateKind.SlidingWindow:
                options.Global.UseSlidingWindow(rate =>
                {
                    rate.PermitLimit = rateLimit;
                    rate.Window = TimeSpan.FromHours(1);
                    rate.SegmentsPerWindow = 2;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void ConfigurePartition(
        SharpLinkAdmissionControlOptions options,
        int globalConcurrency)
    {
        options.Global.UseConcurrency(globalConcurrency);
        options.UsePartition(TenantSelector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = 1;
                rate.TokensPerPeriod = 1;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
        });
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

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    public enum RateKind
    {
        TokenBucket,
        FixedWindow,
        SlidingWindow
    }
}
