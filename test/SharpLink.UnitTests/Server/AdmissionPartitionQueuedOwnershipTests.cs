using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionPartitionQueuedOwnershipTests
{
    private static readonly TimeSpan QueueDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(1);

    [Test]
    public async Task QueuedSuccessShouldTransferPartitionOwnershipExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var controller = CreateController(time);

        var first = await controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None);
        Ensure(first.IsAcquired, "first request should acquire the partition permit");

        var pending = controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(!pending.IsCompleted, "second request should remain queued across an await");

        first.Lease!.Dispose();
        var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(second.IsAcquired, "queued request should acquire after the active lease releases");
        second.Lease!.Dispose();

        EnsureQueueDrained(controller);
        await EnsureCapacityRecoversAsync(controller, time);
    }

    [Test]
    public async Task QueueTimeoutShouldReleasePartitionOwnershipExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var controller = CreateController(time);

        var first = await controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(!pending.IsCompleted, "second request should be queued before the timeout fires");

        time.Advance(QueueDelay);
        var timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!timedOut.IsAcquired && timedOut.Reason == "concurrency",
            "queue timeout should surface the failed partition concurrency slot");

        first.Lease!.Dispose();
        EnsureQueueDrained(controller);
        await EnsureCapacityRecoversAsync(controller, time);
    }

    [Test]
    public async Task CallerCancellationShouldReleaseQueuedPartitionOwnershipExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var controller = CreateController(time);
        using var cancellation = new CancellationTokenSource();

        var first = await controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, cancellation.Token).AsTask();
        Ensure(!pending.IsCompleted, "second request should be queued before caller cancellation");

        cancellation.Cancel();
        await EnsureCanceledAsync(pending);

        first.Lease!.Dispose();
        EnsureQueueDrained(controller);
        await EnsureCapacityRecoversAsync(controller, time);
    }

    [Test]
    public async Task DeadlineCancellationShouldReleaseQueuedPartitionOwnershipExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var controller = CreateController(time);

        var first = await controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None);
        var deadline = time.GetUtcNow().Add(QueueDelay / 2);
        var pending = controller.AcquireAsync(
            CreateContext("hot", deadline), 1, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(!pending.IsCompleted, "second request should be queued before its deadline");

        time.Advance(QueueDelay / 2);
        var expired = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!expired.IsAcquired &&
               expired.Reason == "deadline" &&
               expired.ErrorCode == SharpLinkErrorCode.DeadlineExceeded,
            "deadline-limited queue wait should surface DeadlineExceeded");

        first.Lease!.Dispose();
        EnsureQueueDrained(controller);
        await EnsureCapacityRecoversAsync(controller, time);
    }

    [Test]
    public async Task DrainingShouldReleaseQueuedPartitionOwnershipExactlyOnce()
    {
        var time = new ManualTimeProvider();
        await using var controller = CreateController(time);

        var first = await controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None);
        var pending = controller.AcquireAsync(
            CreateContext("hot"), 1, allowQueue: true, CancellationToken.None).AsTask();
        Ensure(!pending.IsCompleted, "second request should be queued before draining starts");

        controller.StopAccepting();
        var drained = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!drained.IsAcquired &&
               drained.Reason == "draining" &&
               drained.ErrorCode == SharpLinkErrorCode.Unavailable,
            "draining should terminate the queued request as unavailable");

        first.Lease!.Dispose();
        EnsureQueueDrained(controller);

        // StopAccepting intentionally prevents another controller acquisition. Probe the same
        // resident pool directly so this terminal path still proves the queued request released
        // its partition reference and the old key can be reclaimed at capacity.
        var pool = GetPartitionPool(controller);
        time.Advance(IdleTimeout);
        var replacement = pool.TryAcquire(CreateContext("replacement"));
        Ensure(replacement is not null,
            "draining must release the queued partition reference so capacity can be reclaimed");
        pool.Release(replacement!);
    }

    private static SharpLinkAdmissionController CreateController(ManualTimeProvider time)
    {
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = QueueDelay
        };
        options.UsePartition(
            static context => context.ConnectionId,
            partition =>
            {
                partition.MaxPartitions = 1;
                partition.IdleTimeout = IdleTimeout;
                partition.UseConcurrency(1);
            });
        return SharpLinkAdmissionController.Create(options, [], time);
    }

    private static SharpLinkAdmissionContext CreateContext(
        string partition,
        DateTimeOffset? deadline = null)
        => new(1, 2, RpcMethodKind.Unary, partition, null, null, deadline);

    private static async Task EnsureCapacityRecoversAsync(
        SharpLinkAdmissionController controller,
        ManualTimeProvider time)
    {
        time.Advance(IdleTimeout);
        var replacement = await controller.AcquireAsync(
            CreateContext("replacement"), 1, allowQueue: false, CancellationToken.None);
        Ensure(replacement.IsAcquired,
            "terminal queue path must release the old partition reference so MaxPartitions capacity recovers");
        replacement.Lease!.Dispose();
    }

    private static async Task EnsureCanceledAsync(Task<AdmissionDecision> pending)
    {
        try
        {
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("caller cancellation should propagate");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void EnsureQueueDrained(SharpLinkAdmissionController controller)
    {
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0,
            "terminal queue path must release bounded queue accounting exactly once");
        Ensure(controller.ActivePermits == 0,
            "all admitted concurrency permits should be released before the reclaim probe");
    }

    private static AdmissionPartitionPool GetPartitionPool(SharpLinkAdmissionController controller)
    {
        var field = typeof(SharpLinkAdmissionController).GetField(
            "_partitions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Ensure(field is not null, "partition pool backing field should remain discoverable for the draining probe");
        var pool = field!.GetValue(controller) as AdmissionPartitionPool;
        Ensure(pool is not null, "partition-enabled controller should own a partition pool");
        return pool!;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
            lock (_gate)
            {
                _utcNow = _utcNow.Add(delta);
                _timestamp = checked(_timestamp + delta.Ticks);
            }

            while (true)
            {
                ManualTimer[] due;
                lock (_gate)
                {
                    due = _timers
                        .Where(timer => !timer.IsDisposed && timer.DueTimestamp <= _timestamp)
                        .ToArray();
                    foreach (var timer in due)
                    {
                        timer.DueTimestamp = timer.PeriodTicks > 0
                            ? checked(timer.DueTimestamp + timer.PeriodTicks)
                            : long.MaxValue;
                    }
                }
                if (due.Length == 0)
                    return;
                foreach (var timer in due)
                    timer.Invoke();
            }
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimerDuration(dueTime, nameof(dueTime));
            ValidateTimerDuration(period, nameof(period));
            lock (_gate)
            {
                if (timer.IsDisposed)
                    return false;
                if (!_timers.Contains(timer))
                    _timers.Add(timer);
                timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(_timestamp + dueTime.Ticks);
                timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_gate)
            {
                timer.IsDisposed = true;
                _timers.Remove(timer);
            }
        }

        private static void ValidateTimerDuration(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            internal long DueTimestamp = long.MaxValue;
            internal long PeriodTicks;
            internal bool IsDisposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.Change(this, dueTime, period);

            public void Dispose() => owner.Dispose(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void Invoke()
            {
                if (!IsDisposed)
                    callback(state);
            }
        }
    }
}
