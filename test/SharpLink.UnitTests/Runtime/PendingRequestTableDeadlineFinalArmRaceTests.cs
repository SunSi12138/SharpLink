using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableDeadlineFinalArmRaceTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ScannerFinalArmMustNotOverwriteConcurrentlyRegisteredEarlierDeadline()
    {
        var timeProvider = new FinalArmRaceTimeProvider(blockChangeNumber: 3);
        using var table = new PendingRequestTable(
            8,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            timeProvider);

        var laterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), timeProvider);
        var later = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            laterDeadline,
            CancellationToken.None,
            out var laterId).AsValueTask().AsTask();

        var scan = Task.Run(timeProvider.FireTimer);
        Ensure(timeProvider.BlockedChangeEntered.Wait(CoordinationTimeout),
            "the scanner should reach its deterministic final-arm gate");

        var earlierDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var earlier = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            earlierDeadline,
            CancellationToken.None,
            out _).AsValueTask().AsTask();

        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "the concurrent earlier registration should arm the one-second deadline before the stale scanner arm is released");

        timeProvider.ReleaseBlockedChange.Set();
        await scan.WaitAsync(CoordinationTimeout);

        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "scanner finalization must reconcile after its stale arm and leave the earlier deadline scheduled");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        timeProvider.FireIfDue();
        var earlierFailure = await CaptureExceptionAsync(earlier);
        Ensure(earlierFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the earlier call must expire at its own monotonic deadline");
        Ensure(!later.IsCompleted,
            "the later call must remain pending when the earlier deadline expires");

        Ensure(table.TryComplete(
            laterId,
            PendingCallCompletionReason.ConnectionClosed,
            new IOException("test cleanup")),
            "later call cleanup");
        Ensure(await CaptureExceptionAsync(later) is IOException,
            "later call cleanup result");
    }

    [Test]
    public async Task ReconcileMustValidateActualEarliestValueAfterStaleArm()
    {
        var timeProvider = new FinalArmRaceTimeProvider(blockChangeNumber: 2);
        using var table = new PendingRequestTable(
            8,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            timeProvider);

        var laterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), timeProvider);
        var later = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            laterDeadline,
            CancellationToken.None,
            out var laterId).AsValueTask().AsTask();

        var tableType = typeof(PendingRequestTable);
        var reconcile = tableType.GetMethod(
            "ReconcileDeadlineTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(tableType.FullName, "ReconcileDeadlineTimer");
        var arm = tableType.GetMethod(
            "ArmDeadlineTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(tableType.FullName, "ArmDeadlineTimer");
        var earliest = tableType.GetField(
            "_approximateEarliestDeadline",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(tableType.FullName, "_approximateEarliestDeadline");
        var revision = tableType.GetField(
            "_deadlineRevision",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(tableType.FullName, "_deadlineRevision");

        var reconcileTask = Task.Run(() => reconcile.Invoke(table, parameters: null));
        Ensure(timeProvider.BlockedChangeEntered.Wait(CoordinationTimeout),
            "reconciliation should sample the ten-second earliest value before its stale arm is applied");

        var earlierDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);

        // Model the review interleaving directly: schedule identity has already been observed,
        // then the actual earliest deadline moves earlier before the stale arm completes. Using
        // reflection here avoids adding a production-only test hook to the registration hot path.
        earliest.SetValue(table, earlierDeadline);
        revision.SetValue(table, (long)revision.GetValue(table)! + 1);
        arm.Invoke(table, [earlierDeadline]);
        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "the simulated earlier writer should arm the one-second deadline first");

        timeProvider.ReleaseBlockedChange.Set();
        await reconcileTask.WaitAsync(CoordinationTimeout);

        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "a stale ten-second arm must be rejected by validating the actual shared earliest value");

        Ensure(table.TryComplete(
            laterId,
            PendingCallCompletionReason.ConnectionClosed,
            new IOException("test cleanup")),
            "later call cleanup");
        Ensure(await CaptureExceptionAsync(later) is IOException,
            "later call cleanup result");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FinalArmRaceTimeProvider(int blockChangeNumber) : TimeProvider
    {
        private static readonly DateTimeOffset Origin = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly object _gate = new();
        private ControlledTimer? _timer;
        private long _timestamp;
        private int _changeCount;

        internal ManualResetEventSlim BlockedChangeEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim ReleaseBlockedChange { get; } = new(initialState: false);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
            => Origin.AddTicks(Volatile.Read(ref _timestamp));

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_gate)
            {
                if (_timer is not null)
                    throw new InvalidOperationException("the pending table must own exactly one timer");
                _timer = new ControlledTimer(this, callback, state);
                if (dueTime != Timeout.InfiniteTimeSpan)
                    _timer.Change(dueTime, period);
                return _timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
            lock (_gate)
            {
                if (_timer is not { IsDisposed: false } timer ||
                    timer.ScheduledDelay == Timeout.InfiniteTimeSpan ||
                    timer.ScheduledDelay <= TimeSpan.Zero)
                {
                    return;
                }

                timer.ScheduledDelay = elapsed >= timer.ScheduledDelay
                    ? TimeSpan.Zero
                    : timer.ScheduledDelay - elapsed;
            }
        }

        internal void FireTimer()
        {
            ControlledTimer timer;
            lock (_gate)
                timer = _timer ?? throw new InvalidOperationException("timer not created");
            timer.Fire();
        }

        internal void FireIfDue()
        {
            ControlledTimer timer;
            lock (_gate)
            {
                timer = _timer ?? throw new InvalidOperationException("timer not created");
                if (timer.ScheduledDelay > TimeSpan.Zero)
                    return;
            }
            timer.Fire();
        }

        internal TimeSpan GetScheduledDelay()
        {
            lock (_gate)
                return _timer?.ScheduledDelay ?? Timeout.InfiniteTimeSpan;
        }

        private bool ChangeTimer(ControlledTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            var changeNumber = Interlocked.Increment(ref _changeCount);
            if (changeNumber == blockChangeNumber)
            {
                BlockedChangeEntered.Set();
                if (!ReleaseBlockedChange.Wait(CoordinationTimeout))
                    throw new TimeoutException("test did not release the blocked timer change");
            }

            lock (_gate)
            {
                if (timer.IsDisposed)
                    return false;
                timer.ScheduledDelay = dueTime;
                return true;
            }
        }

        private sealed class ControlledTimer(
            FinalArmRaceTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            internal TimeSpan ScheduledDelay { get; set; } = Timeout.InfiniteTimeSpan;
            internal bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.ChangeTimer(this, dueTime, period);

            public void Dispose()
            {
                lock (owner._gate)
                {
                    IsDisposed = true;
                    ScheduledDelay = Timeout.InfiniteTimeSpan;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void Fire()
            {
                lock (owner._gate)
                {
                    if (IsDisposed)
                        return;
                    ScheduledDelay = Timeout.InfiniteTimeSpan;
                }
                callback(state);
            }
        }
    }

    private sealed class Int32CodecProvider : IRpcCodecProvider
    {
        internal static readonly Int32CodecProvider Instance = new();

        public IRpcCodec<T> GetCodec<T>()
            => typeof(T) == typeof(int)
                ? (IRpcCodec<T>)(object)Int32Codec.Instance
                : throw new NotSupportedException(typeof(T).FullName);
    }

    private sealed class NoopOwner : IPendingCallOwner
    {
        internal static readonly NoopOwner Instance = new();

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }
}
