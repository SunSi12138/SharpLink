using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PendingRequestSegmentationEvidenceRunner
{
    private const int Capacity = 65_536;
    private static readonly Exception CleanupException = new IOException("pending-segmentation evidence cleanup");

    public static async Task RunAsync(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Expected evidence mode: memory, construction, scan, or lateness.");

        switch (args[0])
        {
            case "memory":
                RunMemory(args[1..]);
                return;
            case "construction":
                RunConstruction(args[1..]);
                return;
            case "scan":
                RunScan(args[1..]);
                return;
            case "lateness":
                await RunLatenessAsync(args[1..]).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(args), args[0], "Unknown pending-segmentation evidence mode.");
        }
    }

    private static void RunMemory(string[] args)
    {
        var active = GetInt32(args, "--active", required: true);
        var connections = GetInt32(args, "--connections", 1000);
        if (active is not (0 or 1 or 8))
            throw new ArgumentOutOfRangeException(nameof(args), "Memory evidence active count must be 0, 1, or 8.");

        WarmUp();
        var tables = new PendingRequestTable[connections];
        var operations = new RpcRequestOperation<int>[checked(connections * active)];
        ForceFullGc();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var operationIndex = 0;
        for (var connection = 0; connection < connections; connection++)
        {
            var table = CreateTable(TimeProvider.System);
            tables[connection] = table;
            for (var index = 0; index < active; index++)
                operations[operationIndex++] = table.Rent<int>(out _);
        }

        ForceFullGc();
        var after = GC.GetTotalMemory(forceFullCollection: true);
        var retained = Math.Max(0, after - before);
        var result = new
        {
            mode = "memory",
            implementation = GetImplementation(tables[0]),
            segmentSize = GetOptionalInt32(tables[0], "SegmentSize"),
            capacity = Capacity,
            active,
            connections,
            retainedBytes = retained,
            retainedBytesPerConnection = retained / (double)connections,
            materializedSegmentsPerConnection = GetOptionalInt32(tables[0], "MaterializedSegmentCount")
        };
        Console.WriteLine(JsonSerializer.Serialize(result));

        Cleanup(tables, operations);
    }

    private static void RunConstruction(string[] args)
    {
        var connections = GetInt32(args, "--connections", 1000);
        WarmUp();
        var tables = new PendingRequestTable[connections];
        ForceFullGc();
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();

        for (var index = 0; index < tables.Length; index++)
            tables[index] = CreateTable(TimeProvider.System);

        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        var result = new
        {
            mode = "construction",
            implementation = GetImplementation(tables[0]),
            segmentSize = GetOptionalInt32(tables[0], "SegmentSize"),
            capacity = Capacity,
            connections,
            nanosecondsPerConnection = elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / connections,
            allocatedBytesPerConnection = allocatedBytes / (double)connections
        };
        Console.WriteLine(JsonSerializer.Serialize(result));

        Cleanup(tables, []);
    }

    private static void RunScan(string[] args)
    {
        var active = GetInt32(args, "--active", required: true);
        var deadlines = GetInt32(args, "--deadlines", required: true);
        var iterations = GetInt32(args, "--iterations", 10_000);
        if (active <= 0 || deadlines <= 0 || deadlines > active)
            throw new ArgumentOutOfRangeException(nameof(args), "Scan evidence requires 0 < deadlines <= active.");

        var timeProvider = new ManualEvidenceTimeProvider();
        using var table = CreateTable(timeProvider);
        var operations = new RpcRequestOperation<int>[active];
        var deadline = RpcDeadline.Create(timeProvider.GetUtcNow().AddHours(1), timeProvider);
        for (var index = 0; index < active; index++)
        {
            operations[index] = index < deadlines
                ? table.Rent(
                    Int32Codec.Instance,
                    PendingCallKind.Unary,
                    deadline,
                    CancellationToken.None,
                    out _)
                : table.Rent<int>(out _);
        }

        var method = typeof(PendingRequestTable).GetMethod(
            "ScanExpiredDeadlines",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PendingRequestTable).FullName, "ScanExpiredDeadlines");
        var scan = (Action<PendingRequestTable>)method.CreateDelegate(typeof(Action<PendingRequestTable>));
        for (var index = 0; index < 100; index++)
            scan(table);

        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
            scan(table);
        var elapsedTicks = Stopwatch.GetTimestamp() - started;

        var result = new
        {
            mode = "scan",
            implementation = GetImplementation(table),
            segmentSize = GetOptionalInt32(table, "SegmentSize"),
            capacity = Capacity,
            active,
            deadlines,
            iterations,
            inspectedSlots = GetOptionalInt32(table, "LastDeadlineScanInspectedSlots") ?? Capacity,
            materializedSegments = GetOptionalInt32(table, "MaterializedSegmentCount"),
            nanosecondsPerScan = elapsedTicks * 1_000_000_000d / Stopwatch.Frequency / iterations
        };
        Console.WriteLine(JsonSerializer.Serialize(result));

        table.FailAllPendingRequests(CleanupException);
        ObserveFailures(operations);
    }

    private static async Task RunLatenessAsync(string[] args)
    {
        var iterations = GetInt32(args, "--iterations", 40);
        var deadlineMilliseconds = GetInt32(args, "--deadline-ms", 20);
        if (iterations <= 0 || deadlineMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(args));

        using var table = CreateTable(TimeProvider.System);
        await ObserveOneDeadlineAsync(table, 5).ConfigureAwait(false);
        var lateness = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var deadline = RpcDeadline.Create(
                TimeProvider.System.GetUtcNow().AddMilliseconds(deadlineMilliseconds),
                TimeProvider.System);
            var operation = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.Unary,
                deadline,
                CancellationToken.None,
                out _);
            await ObserveDeadlineFailureAsync(operation).ConfigureAwait(false);
            var elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            lateness[index] = Math.Max(0, elapsedMilliseconds - deadlineMilliseconds);
        }

        Array.Sort(lateness);
        var result = new
        {
            mode = "lateness",
            implementation = GetImplementation(table),
            segmentSize = GetOptionalInt32(table, "SegmentSize"),
            capacity = Capacity,
            iterations,
            deadlineMilliseconds,
            p50LatenessMilliseconds = Percentile(lateness, 0.50),
            p95LatenessMilliseconds = Percentile(lateness, 0.95),
            maxLatenessMilliseconds = lateness[^1],
            inspectedSlots = GetOptionalInt32(table, "LastDeadlineScanInspectedSlots") ?? Capacity
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static async Task ObserveOneDeadlineAsync(PendingRequestTable table, int milliseconds)
    {
        var deadline = RpcDeadline.Create(
            TimeProvider.System.GetUtcNow().AddMilliseconds(milliseconds),
            TimeProvider.System);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            deadline,
            CancellationToken.None,
            out _);
        await ObserveDeadlineFailureAsync(operation).ConfigureAwait(false);
    }

    private static async Task ObserveDeadlineFailureAsync(RpcRequestOperation<int> operation)
    {
        try
        {
            _ = await operation.AsValueTask().ConfigureAwait(false);
            throw new InvalidOperationException("Deadline evidence call completed successfully.");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
        {
        }
    }

    private static void WarmUp()
    {
        using var table = CreateTable(TimeProvider.System, capacity: 64);
        var operation = table.Rent<int>(out var id);
        if (!table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed, CleanupException))
            throw new InvalidOperationException("Evidence warm-up could not complete its pending request.");
        ObserveFailures([operation]);
    }

    private static PendingRequestTable CreateTable(TimeProvider timeProvider, int capacity = Capacity)
        => new(
            capacity,
            Int32CodecProvider.Instance,
            NoopOwner.Instance,
            timeProvider);

    private static void Cleanup(
        PendingRequestTable[] tables,
        RpcRequestOperation<int>[] operations)
    {
        foreach (var table in tables)
        {
            table.FailAllPendingRequests(CleanupException);
            table.Dispose();
        }
        ObserveFailures(operations);
    }

    private static void ObserveFailures(RpcRequestOperation<int>[] operations)
    {
        foreach (var operation in operations)
        {
            try
            {
                _ = operation.AsValueTask().GetAwaiter().GetResult();
            }
            catch (IOException exception) when (ReferenceEquals(exception, CleanupException))
            {
            }
        }
    }

    private static string GetImplementation(PendingRequestTable table)
        => GetOptionalInt32(table, "SegmentSize") is null ? "eager" : "segmented";

    private static int? GetOptionalInt32(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.GetValue(instance) is int value ? value : null;
    }

    private static int GetInt32(
        string[] args,
        string name,
        int defaultValue = 0,
        bool required = false)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
                return int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (required)
            throw new ArgumentException($"Missing required argument '{name}'.");
        return defaultValue;
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(sortedValues.Length * percentile) - 1,
            0,
            sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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

    private sealed class ManualEvidenceTimeProvider : TimeProvider
    {
        private long _timestamp = 0;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

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

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
