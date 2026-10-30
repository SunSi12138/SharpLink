using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static partial class PendingRequestMatrixEvidenceRunner
{
    private static (double P50, double P95, double P99, double P999, double Max) MeasureSequentialProbe(
        PendingRequestTable table,
        int operations)
    {
        var latencies = new long[operations];
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var operation = table.Rent<int>(out var id);
            CompleteSuccess(table, operation, id);
            latencies[index] = Stopwatch.GetTimestamp() - started;
        }
        var stats = TimingStatistics(latencies);
        return (stats.P50, stats.P95, stats.P99, stats.P999, stats.Max);
    }

    private static PendingLease[] Fill(PendingRequestTable table, int count)
    {
        var result = new PendingLease[count];
        for (var index = 0; index < count; index++)
        {
            var operation = table.Rent<int>(out var id);
            result[index] = new PendingLease(id, operation);
        }
        Require(table.ActiveCount <= table.Capacity, "Fill exceeded pending capacity.");
        return result;
    }

    private static void CompleteSuccess(PendingRequestTable table, RpcRequestOperation<int> operation, long id)
    {
        var payload = new ReadOnlySequence<byte>(ResponsePayload);
        Require(table.Dispatch(id, ref payload), "Pending response did not match its live request.");
        _ = operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void ObserveDeadlineFailure(RpcRequestOperation<int> operation)
    {
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Deadline operation completed successfully.");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
        {
        }
    }

    private static void ObserveFailures(
        IEnumerable<RpcRequestOperation<int>> operations,
        Func<Exception, bool> expected)
    {
        foreach (var operation in operations)
        {
            try
            {
                _ = operation.AsValueTask().GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected pending operation failure completed successfully.");
            }
            catch (Exception exception) when (expected(exception))
            {
            }
        }
    }

    private static PendingRequestTable CreateTable(int capacity, TimeProvider timeProvider, RecordingOwner owner)
        => new(capacity, Int32CodecProvider.Instance, owner, timeProvider);

    private static int GetWaiterCount(PendingRequestTable table)
        => (int)WaiterCountField.GetValue(table)!;

    private static long GetNextId(PendingRequestTable table)
        => (long)NextIdField.GetValue(table)!;

    private static FieldInfo GetRequiredField(string name)
        => typeof(PendingRequestTable).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingFieldException(typeof(PendingRequestTable).FullName, name);

    private static Action<PendingRequestTable> GetDeadlineScanDelegate()
    {
        var method = typeof(PendingRequestTable).GetMethod(
            "ScanExpiredDeadlines",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PendingRequestTable).FullName, "ScanExpiredDeadlines");
        return (Action<PendingRequestTable>)method.CreateDelegate(typeof(Action<PendingRequestTable>));
    }

    private static Distribution TimingStatistics(long[] ticks)
    {
        var nanoseconds = new long[ticks.Length];
        for (var index = 0; index < ticks.Length; index++)
            nanoseconds[index] = (long)Math.Round(ToNanoseconds(ticks[index]));
        return Statistics(nanoseconds);
    }

    private static Distribution Statistics(long[] values)
    {
        if (values.Length == 0)
            return new Distribution(0, 0, 0, 0, 0, 0, 0);
        var sorted = (long[])values.Clone();
        Array.Sort(sorted);
        double total = 0;
        foreach (var value in sorted)
            total += value;
        return new Distribution(
            sorted[0],
            total / sorted.Length,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            Percentile(sorted, 0.999),
            sorted[^1]);
    }

    private static double Percentile(long[] sortedValues, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sortedValues.Length * percentile) - 1, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sortedValues.Length * percentile) - 1, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static double ToNanoseconds(long ticks)
        => ticks * 1_000_000_000d / Stopwatch.Frequency;

    private static double ToSeconds(long ticks)
        => ticks / (double)Stopwatch.Frequency;

    private static string GetString(string[] args, string name, string defaultValue)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return args[index + 1];
        return defaultValue;
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct OccupancyCell(int Capacity, int OccupancyPercent, int Producers, int OperationsPerProducer);
    private readonly record struct SparseDeadlineCell(int Capacity, int Active, int Deadlines, int Iterations, string Pattern);
    private readonly record struct LongShortCell(
        int Capacity,
        int OccupancyPercent,
        int LongPercent,
        int Producers,
        string TerminalMode,
        int OperationsPerProducer);
    private readonly record struct PendingLease(long Id, RpcRequestOperation<int> Operation);
    private readonly record struct Distribution(
        double Min,
        double Average,
        double P50,
        double P95,
        double P99,
        double P999,
        double Max);

    private static void RunCompletionLedgerGates(List<object> cells)
    {
        static PendingCallCompletion Completion(long id)
            => new(id, PendingCallKind.Unary, PendingCallCompletionReason.Response, null, null, true);
        static void RequireFailure(Action action, string expected)
        {
            try { action(); }
            catch (InvalidOperationException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal))
            { return; }
            throw new InvalidOperationException($"Completion ledger did not detect {expected}.");
        }

        var missing = new RecordingOwner();
        missing.ExpectCompletions(1);
        RequireFailure(missing.RequireIdle, "missing 1");
        missing.OnPendingCallCompleted(Completion(1));
        missing.RequireIdle();

        var duplicate = new RecordingOwner();
        duplicate.ExpectCompletions(2);
        duplicate.OnPendingCallCompleted(Completion(1));
        RequireFailure(() => duplicate.OnPendingCallCompleted(Completion(1)), "more than once");
        // Callback exceptions may be isolated by the product: RequireIdle must still fail.
        RequireFailure(duplicate.RequireIdle, "more than once");

        var extra = new RecordingOwner();
        extra.ExpectCompletions(1);
        extra.OnPendingCallCompleted(Completion(1));
        RequireFailure(() => extra.OnPendingCallCompleted(Completion(2)), "underflowed");
        RequireFailure(extra.RequireIdle, "underflowed");
        cells.Add(new
        {
            category = "hard-gate",
            scenario = "completion-ledger-missing-duplicate-extra-controls",
            missingDetected = true,
            duplicateDetected = true,
            extraDetected = true,
            recoveredPhaseHistoryReleased = true,
            invariant = true
        });
    }

    private sealed class RecordingOwner : IPendingCallOwner
    {
        private readonly object _gate = new();
        private HashSet<long> _completedIds = [];
        private int _remaining;
        private string? _failure;

        // Compatibility member for replaying this same harness on the historical interface.
        // Registration ordering is tested by the pre-MarkRegistered unit/probe witnesses,
        // not by this phase-level completion ledger.
        public void OnPendingCallRegistered() { }

        public void ExpectCompletions(int count)
        {
            lock (_gate)
            {
                RequireIdle();
                if (count <= 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                _remaining = count;
            }
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            lock (_gate)
            {
                if (!_completedIds.Add(completion.RequestId))
                    _failure ??= $"Request {completion.RequestId} completed more than once.";
                if (--_remaining < 0)
                    _failure ??= "Pending owner completion accounting underflowed.";
                if (_failure is not null)
                    throw new InvalidOperationException(_failure);
                if (_remaining == 0)
                {
                    // Drop the whole phase's harness history before recovery heap samples.
                    // Revised timings require this same phase ledger on BOTH source arms.
                    _completedIds = [];
                }
            }
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
            => throw new InvalidOperationException("Producer cancellation callback failed during matrix evidence.", exception);

        public void RequireIdle()
        {
            lock (_gate)
            {
                if (_failure is not null)
                    throw new InvalidOperationException(_failure);
                if (_remaining != 0)
                    throw new InvalidOperationException($"Pending owner is missing {_remaining} expected completions.");
                if (_completedIds.Count != 0)
                    throw new InvalidOperationException("Pending owner retained its completed phase history.");
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

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(Volatile.Read(ref _timestamp));

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delta));
            Interlocked.Add(ref _timestamp, delta.Ticks);
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
            => NoopTimer.Instance;

        private sealed class NoopTimer : ITimer
        {
            internal static NoopTimer Instance { get; } = new();
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }
}
