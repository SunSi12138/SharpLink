using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.LoadTestBase;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.LoadTest;

public sealed record StageResult(
    string Operation,
    int Concurrency,
    long Success,
    long Failure,
    long SendQueueBackpressureRetries,
    double Qps,
    double OneWayPayloadMegabytesPerSecond,
    double RoundTripPayloadMegabytesPerSecond,
    double? P50Us,
    double? P95Us,
    double? P99Us,
    double? P999Us,
    double? AvgUs,
    double? MinUs,
    double? MaxUs,
    double WarmupDurationSeconds,
    double MeasurementDurationSeconds,
    double DrainDurationSeconds,
    long OperationsStartedDuringMeasurement,
    long OperationsCompleted,
    long SampleCount,
    int MaximumSampleCapacity,
    string RecorderMode,
    string RecorderVersion,
    long StopwatchFrequency,
    bool FormalComparable,
    long TailObserverSampleCount,
    long TailObserverFailure,
    double? TailObserverP99Us,
    double? TailObserverP999Us,
    double ErrorRatePercent,
    string TopFailures,
    PerformanceStageEvidence Evidence)
{
    public int WorkerCount => Concurrency;
}

public sealed record RealtimeResult(
    string Operation,
    int Concurrency,
    double Qps,
    double P50Us,
    double P95Us,
    double P99Us,
    double P999Us);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerformanceReport<LoadTestOptions, StageResult>))]
[JsonSerializable(typeof(PerformanceReport<LoadTestOptions, HoldCapacityResult>))]
internal sealed partial class LoadTestJsonContext : JsonSerializerContext;

internal readonly record struct WorkerStageOutcome(
    long Success,
    long Failure,
    long SendQueueBackpressureRetries,
    long OperationsStarted);

internal enum PendingLoadOperationKind
{
    Void,
    Int32,
    String
}

internal readonly record struct PendingLoadOperation(
    PendingLoadOperationKind Kind,
    ValueTask VoidCompletion,
    ValueTask<int> Int32Completion,
    ValueTask<string> StringCompletion)
{
    public static PendingLoadOperation From(ValueTask completion)
        => new(PendingLoadOperationKind.Void, completion, default, default);

    public static PendingLoadOperation From(ValueTask<int> completion)
        => new(PendingLoadOperationKind.Int32, default, completion, default);

    public static PendingLoadOperation From(ValueTask<string> completion)
        => new(PendingLoadOperationKind.String, default, default, completion);
}

internal readonly record struct TailObserverOutcome(long SampleCount, long Failure)
{
    public static TailObserverOutcome Empty { get; } = new(0, 0);
}
