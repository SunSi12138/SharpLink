using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableConcurrentDeadlineArmRaceTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ConcurrentRegistrationsMustNotLeaveStaleLaterTimerArmed()
    {
        var timeProvider = new RegistrationArmRaceTimeProvider(blockChangeNumber: 1);
        using var table = new PendingRequestTable(
            8,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            timeProvider);

        var laterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), timeProvider);
        var laterRegistration = Task.Run(() =>
        {
            var operation = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.Unary,
                laterDeadline,
                CancellationToken.None,
                out var id).AsValueTask().AsTask();
            return (Operation: operation, Id: id);
        });

        Ensure(timeProvider.BlockedChangeEntered.Wait(CoordinationTimeout),
            "the first registration should reach its deterministic timer-arm gate");

        var earlierDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var earlier = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            earlierDeadline,
            CancellationToken.None,
            out _).AsValueTask().AsTask();

        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "the concurrent earlier registration should arm its one-second deadline");

        timeProvider.ReleaseBlockedChange.Set();
        var later = await laterRegistration.WaitAsync(CoordinationTimeout);

        Ensure(timeProvider.GetScheduledDelay() == TimeSpan.FromSeconds(1),
            "the stale later registration arm must be reconciled back to the shared earliest deadline");

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        timeProvider.FireIfDue();
        var earlierFailure = await CaptureExceptionAsync(earlier);
        Ensure(earlierFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the earlier call must expire at its own monotonic deadline");
        Ensure(!later.Operation.IsCompleted,
            "the later call must remain pending when the earlier deadline expires");

        Ensure(table.TryComplete(
            later.Id,
            PendingCallCompletionReason.ConnectionClosed,
            new IOException("test cleanup")),
            "later call cleanup");
        Ensure(await CaptureExceptionAsync(later.Operation) is IOException,
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

    private sealed class RegistrationArmRaceTimeProvider(int blockChangeNumber) : TimeProvider
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

        internal void FireIfDue()
            => GetTimer().FireIfDue(GetTimestamp());

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
                    throw new TimeoutException("test did not release the registration timer-arm gate");
            }

            timer.SetScheduledTimestamp(
                dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(GetTimestamp() + Math.Max(0, dueTime.Ticks)));
            return true;
        }

        private sealed class ControlledTimer(
            RegistrationArmRaceTimeProvider owner,
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
