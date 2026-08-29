using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionDynamicPartitionUpdateTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> ConnectionSelector =
        static context => context.ConnectionId;
    private static readonly Func<SharpLinkAdmissionContext, string?> ReplacementSelector =
        static context => $"replacement:{context.ConnectionId}";

    [Test]
    public async Task MaxPartitionsShrinkShouldPreserveLiveEntriesAndRejectOnlyNewKeys()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;

        var first = await source.Controller.AcquireAsync(Context("tenant-a"), 1, false, CancellationToken.None);
        var second = await source.Controller.AcquireAsync(Context("tenant-b"), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired && second.IsAcquired && pool.Count == 2,
            "two pre-shrink entries must be live before the target changes");

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 1));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "same-selector MaxPartitions shrink must retain one authoritative namespace pool");
        Ensure(pool.Count == 2,
            "shrink below the live count must not evict active entries");

        var rejected = await replacement.Controller.AcquireAsync(
            Context("tenant-c"), 1, false, CancellationToken.None);
        Ensure(!rejected.IsAcquired && rejected.Reason == "partition_capacity",
            "new missing keys must be rejected while live entries remain above the shrunken target");

        first.Lease!.Dispose();
        second.Lease!.Dispose();
    }

    [Test]
    public async Task MaxPartitionsIncreaseShouldReuseNamespaceAndExposeOnlyDeltaCapacity()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;

        var first = await source.Controller.AcquireAsync(Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired, "first key must create the only initial entry");
        first.Lease!.Dispose();
        Ensure(pool.Count == 1, "long idle timeout must retain the first entry");

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "same-selector grow must not create a second pool");

        var second = await replacement.Controller.AcquireAsync(Context("tenant-b"), 1, false, CancellationToken.None);
        Ensure(second.IsAcquired && pool.Count == 2,
            "1 -> 2 must expose exactly one additional entry slot");
        var third = await replacement.Controller.AcquireAsync(Context("tenant-c"), 1, false, CancellationToken.None);
        Ensure(!third.IsAcquired && third.Reason == "partition_capacity" && pool.Count == 2,
            "growth must not mint an overlapping fresh two-entry budget");
        second.Lease!.Dispose();
    }

    [Test]
    public async Task PartitionConcurrencyIncreaseShouldPreserveHolderAndWakeQueuedRequest()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options);
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 1);
        });
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "partition concurrency waiter must own one outer queue reservation");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureQueue(options);
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 2);
        });
        Ensure(ReferenceEquals(pool, Current(server).Controller.PartitionStateForTests),
            "partition concurrency resize must preserve the namespace and existing entry");

        var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(admitted.IsAcquired,
            "1 -> 2 must synchronously expose one additional permit to the existing FIFO waiter");
        Ensure(source.Kernel.QueuedCalls == 0 && pool.Count == 1,
            "resize must not split queue accounting or partition identity");

        holder.Lease!.Dispose();
        admitted.Lease!.Dispose();
    }

    [Test]
    public async Task PartitionTokenBucketIncreaseShouldExposeOnlyDeltaQuota()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionTokenBucket(options, ConnectionSelector, tokenLimit: 1, tokensPerPeriod: 1));
        var source = Current(server);
        var pool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var consumed = await source.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(consumed.IsAcquired, "first request must consume the partition bucket");
        consumed.Lease!.Dispose();

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionTokenBucket(options, ConnectionSelector, tokenLimit: 2, tokensPerPeriod: 2));
        var replacement = Current(server);
        Ensure(ReferenceEquals(pool, replacement.Controller.PartitionStateForTests),
            "rate parameter update must keep the same partition namespace and key state");

        var delta = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(delta.IsAcquired,
            "raising the partition bucket from one to two after one consumed token may expose one delta token");
        delta.Lease!.Dispose();
        var exhausted = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(!exhausted.IsAcquired && exhausted.Reason == "rate",
            "partition rate update must not expose a fresh full target bucket");
    }

    [Test]
    public async Task SelectorReplacementShouldKeepOldQueuedRequestOnOldNamespace()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            ConfigureQueue(options);
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 1);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must retain the old captured program generation");
        var oldPool = source.Controller.PartitionStateForTests!;
        var context = Context("tenant-a");

        var holder = await source.Controller.AcquireAsync(context, 1, true, CancellationToken.None);
        var queued = source.Controller.AcquireAsync(context, 1, true, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => source.Kernel.QueuedCalls == 1,
            "old request must be queued before selector replacement");

        publicServer.UpdateAdmissionControl(options =>
        {
            ConfigureQueue(options);
            ConfigurePartitionConcurrency(options, ReplacementSelector, maxPartitions: 8, permitLimit: 1);
        });
        var replacement = Current(server);
        var newPool = replacement.Controller.PartitionStateForTests!;
        Ensure(!ReferenceEquals(oldPool, newPool),
            "selector replacement must publish a distinct namespace generation");

        var newRequest = await replacement.Controller.AcquireAsync(context, 1, false, CancellationToken.None);
        Ensure(newRequest.IsAcquired,
            "new selector generation must have an independent empty entry dictionary and limiter state");
        Ensure(!queued.IsCompleted,
            "old queued request must remain attached to the captured old namespace");

        holder.Lease!.Dispose();
        var oldAdmitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(oldAdmitted.IsAcquired,
            "old queued request must complete normally after the old holder releases");
        oldAdmitted.Lease!.Dispose();
        newRequest.Lease!.Dispose();
        source.ReleaseUse();
    }

    [Test]
    public async Task LosingUpdateMustNotMutateWinningPartitionTargets()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 2, permitLimit: 1));
        var source = Current(server);
        var first = await source.Controller.AcquireAsync(Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(first.IsAcquired, "source namespace must contain one live key before competing updates");
        first.Lease!.Dispose();

        using var losingBuilt = new ManualResetEventSlim();
        using var releaseLosing = new ManualResetEventSlim();
        var barrierClaimed = 0;
        SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, _) =>
        {
            if (!ReferenceEquals(owner, server) || Interlocked.CompareExchange(ref barrierClaimed, 1, 0) != 0)
                return;
            losingBuilt.Set();
            if (!releaseLosing.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("partition losing-update barrier timed out");
        };

        try
        {
            var losingTask = Task.Run(() => CaptureFailure(() => publicServer.UpdateAdmissionControl(options =>
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 1, permitLimit: 1))));
            Ensure(losingBuilt.Wait(TimeSpan.FromSeconds(5)),
                "losing candidate must finish preparation before the winning update publishes");

            publicServer.UpdateAdmissionControl(options =>
                ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 3, permitLimit: 1));
            releaseLosing.Set();
            var failure = await losingTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure is InvalidOperationException,
                "stale exact-source update must fail after another update wins publication");

            var current = Current(server);
            var second = await current.Controller.AcquireAsync(Context("tenant-b"), 1, false, CancellationToken.None);
            var third = await current.Controller.AcquireAsync(Context("tenant-c"), 1, false, CancellationToken.None);
            var fourth = await current.Controller.AcquireAsync(Context("tenant-d"), 1, false, CancellationToken.None);
            Ensure(second.IsAcquired && third.IsAcquired && !fourth.IsAcquired &&
                   fourth.Reason == "partition_capacity",
                "losing shrink must not leak MaxPartitions=1 into the winning MaxPartitions=3 namespace");
            second.Lease!.Dispose();
            third.Lease!.Dispose();
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            releaseLosing.Set();
        }
    }

    [Test]
    public async Task RetiredSelectorGenerationShouldReclaimAfterLastOldUseDrains()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ConnectionSelector, maxPartitions: 8, permitLimit: 1));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must retain the old selector program");
        var oldLease = await source.Controller.AcquireAsync(
            Context("tenant-a"), 1, false, CancellationToken.None);
        Ensure(oldLease.IsAcquired, "old selector entry must be active before replacement");

        publicServer.UpdateAdmissionControl(options =>
            ConfigurePartitionConcurrency(options, ReplacementSelector, maxPartitions: 8, permitLimit: 1));
        var kernel = source.Kernel;
        Ensure(kernel.PartitionStateCount == 2 && source.IsRetired && !source.IsReclaimed,
            "old and new selector generations may overlap while an old Request remains captured");

        oldLease.Lease!.Dispose();
        source.ReleaseUse();
        Ensure(source.IsReclaimed && kernel.PartitionStateCount == 1,
            "old selector namespace must reclaim exactly after its final program/use ownership drains");
    }

    [Test]
    public async Task IdleTimeoutShrinkShouldUseHistoricalIdleTimestamp()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var source = kernel.CreateProgram(
            PartitionOptions(ConnectionSelector, maxPartitions: 1, idleTimeout: TimeSpan.FromMinutes(20)), []);
        AdmissionProgram? candidate = null;
        try
        {
            var first = await source.Controller.AcquireAsync(
                Context("tenant-a"), 1, false, CancellationToken.None);
            Ensure(first.IsAcquired, "test entry must be created before fake time advances");
            first.Lease!.Dispose();
            time.Advance(TimeSpan.FromMinutes(9));

            candidate = kernel.CreateUpdateProgram(
                source,
                PartitionOptions(ConnectionSelector, maxPartitions: 1, idleTimeout: TimeSpan.FromMinutes(5)),
                [],
                out var plan);
            plan.Commit();

            var replacement = await candidate.Controller.AcquireAsync(
                Context("tenant-b"), 1, false, CancellationToken.None);
            Ensure(replacement.IsAcquired,
                "timeout shrink must make the nine-minute-old idle entry reclaimable without resetting age");
            replacement.Lease!.Dispose();
        }
        finally
        {
            candidate?.Retire();
            source.Retire();
        }
    }

    [Test]
    public async Task IdleTimeoutIncreaseShouldNotResetHistoricalIdleTimestamp()
    {
        var time = new ManualTimeProvider();
        await using var owner = SharpLinkAdmissionController.CreateDisabled(time);
        var kernel = owner.Kernel;
        var source = kernel.CreateProgram(
            PartitionOptions(ConnectionSelector, maxPartitions: 1, idleTimeout: TimeSpan.FromMinutes(5)), []);
        AdmissionProgram? candidate = null;
        try
        {
            var first = await source.Controller.AcquireAsync(
                Context("tenant-a"), 1, false, CancellationToken.None);
            Ensure(first.IsAcquired, "test entry must be created before fake time advances");
            first.Lease!.Dispose();
            time.Advance(TimeSpan.FromMinutes(4));

            candidate = kernel.CreateUpdateProgram(
                source,
                PartitionOptions(ConnectionSelector, maxPartitions: 1, idleTimeout: TimeSpan.FromMinutes(30)),
                [],
                out var plan);
            plan.Commit();
            time.Advance(TimeSpan.FromMinutes(27));

            var replacement = await candidate.Controller.AcquireAsync(
                Context("tenant-b"), 1, false, CancellationToken.None);
            Ensure(replacement.IsAcquired,
                "at t=31m the original t=0 last-use timestamp must make the entry reclaimable under a 30m timeout");
            replacement.Lease!.Dispose();
        }
        finally
        {
            candidate?.Retire();
            source.Retire();
        }
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
            throw new Exception("assert failed: expected enabled admission publication");

    private static SharpLinkAdmissionContext Context(string connectionId)
        => new(101, 202, RpcMethodKind.Unary, connectionId, null, null);

    private static void ConfigurePartitionConcurrency(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        int permitLimit)
    {
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseConcurrency(permitLimit);
        });
    }

    private static void ConfigurePartitionTokenBucket(
        SharpLinkAdmissionControlOptions options,
        Func<SharpLinkAdmissionContext, string?> selector,
        int tokenLimit,
        int tokensPerPeriod)
    {
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = 8;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = tokenLimit;
                rate.TokensPerPeriod = tokensPerPeriod;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
        });
    }

    private static SharpLinkAdmissionControlOptions PartitionOptions(
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        TimeSpan idleTimeout)
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = idleTimeout;
            partition.UseConcurrency(1);
        });
        return options;
    }

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 8;
        options.MaxQueuedBytes = 4096;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        internal void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
        }
    }
}
