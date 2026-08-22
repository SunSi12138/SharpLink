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

        var laterDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(10), timeProvider);
        var later = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            laterDeadline,
            CancellationToken.None,
            out var laterId).AsValueTask().AsTask();

        var scan = Task.Run(timeProvider.FireTimer);
        Ensure(timeProvider.BlockedChangeEntered.Wait(CoordinationTimeout),
            "the scanner should reach its deterministic final-arm gate");

        var earlierDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);
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

        var laterDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(10), timeProvider);
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

        var reconcileTask = Task.Run(() => reconcile.Invoke(table, parameters: null));
        Ensure(timeProvider.BlockedChangeEntered.Wait(CoordinationTimeout),
            "reconciliation should sample the ten-second earliest value before its stale arm is applied");

        var earlierDeadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddSeconds(1), timeProvider);

        // Model the review interleaving directly: schedule identity has already been observed,
        // then the actual earliest deadline moves earlier before the stale arm completes. Using
        // reflection here avoids adding a production-only test hook to the registration hot path.
        earliest.SetValue(table, earlierDeadline.Timestamp);
        arm.Invoke(table, [earlierDeadline.Timestamp]);
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

        internal void Advance(TimeSpan duration)
            => Interlocked.Add(ref _timestamp, duration.Ticks);

        internal void FireTimer()
        {
            var timer = GetTimer();
            timer.FireUnconditionally();
        }

        internal void FireIfDue()
        {
            var timer = GetTimer();
            timer.FireIfDue(GetTimestamp());
        }

        internal TimeSpan GetScheduledDelay()
        {
            var scheduled = GetTimer().ScheduledTimestamp;
            if (scheduled == long.MaxValue)
                return Timeout.InfiniteTimeSpan;
            var remaining = Math.Max(0, scheduled - GetTimestamp());
            return TimeSpan.FromTicks(remaining);
        }

        private ControlledTimer GetTimer()
        {
            lock (_gate)
                return _timer ?? throw new InvalidOperationException("timer has not been created");
        }

        private bool Change(ControlledTimer timer, TimeSpan dueTime)
        {
            var change = Interlocked.Increment(ref _changeCount);
            if (change == blockChangeNumber)
            {
                BlockedChangeEntered.Set();
                if (!ReleaseBlockedChange.Wait(CoordinationTimeout))
                    throw new TimeoutException("test did not release the scanner final-arm gate");
            }

            timer.SetScheduledTimestamp(
                dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(GetTimestamp() + Math.Max(0, dueTime.Ticks)));
            return true;
        }

        private sealed class ControlledTimer(
            FinalArmRaceTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private long _scheduledTimestamp = long.MaxValue;
            private int _disposed;

            internal long ScheduledTimestamp => Volatile.Read(ref _scheduledTimestamp);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (period != Timeout.InfiniteTimeSpan)
                    throw new NotSupportedException("the deadline scheduler must use one-shot timers");
                return owner.Change(this, dueTime);
            }

            internal void SetScheduledTimestamp(long timestamp)
                => Volatile.Write(ref _scheduledTimestamp, timestamp);

            internal void FireUnconditionally()
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                Volatile.Write(ref _scheduledTimestamp, long.MaxValue);
                callback(state);
            }

            internal void FireIfDue(long now)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                while (true)
                {
                    var scheduled = Volatile.Read(ref _scheduledTimestamp);
                    if (scheduled == long.MaxValue || scheduled > now)
                        return;
                    if (Interlocked.CompareExchange(ref _scheduledTimestamp, long.MaxValue, scheduled) == scheduled)
                        break;
                }
                callback(state);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                Volatile.Write(ref _scheduledTimestamp, long.MaxValue);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Int32CodecProvider : IRpcCodecProvider
    {
        internal static Int32CodecProvider Instance { get; } = new();

        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)Int32Codec.Instance;
            throw new NotSupportedException(typeof(T).FullName);
        }
    }

    private sealed class Int32Codec : IRpcCodec<int>
    {
        internal static Int32Codec Instance { get; } = new();

        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            buffer.CopyTo(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
    }

    private sealed class NoopOwner : IPendingCallOwner
    {
        internal static NoopOwner Instance { get; } = new();
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { }
        public void OnProducerCancellationCallbackFailed(Exception exception) { }
    }
}
