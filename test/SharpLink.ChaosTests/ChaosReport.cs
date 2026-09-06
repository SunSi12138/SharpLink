using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ChaosTests;

internal sealed record ChaosReport(
    DateTimeOffset TimestampUtc,
    DateTimeOffset StartedUtc,
    string Status,
    string Phase,
    int? ExitCode,
    bool IsFinal,
    string Commit,
    bool? WorkingTreeDirty,
    string OperatingSystem,
    string Architecture,
    string Runtime,
    double DurationSeconds,
    double ActualElapsedSeconds,
    double CheckpointIntervalSeconds,
    double RestartIntervalSeconds,
    int Concurrency,
    string Transport,
    bool DumpOnFailure,
    bool StopOnUnexpectedFailure,
    int RestartCount,
    long Success,
    IReadOnlyDictionary<string, long> OperationAttempts,
    long ExpectedFailures,
    long UnexpectedFailures,
    long UnobservedTaskExceptions,
    long MaxRecoveryMilliseconds,
    long RetainedMemoryStart,
    long RetainedMemoryEnd,
    double RetainedMemoryGrowthPercent,
    double? LastSixHoursRetainedMemoryGrowthPercent,
    IReadOnlyList<MemorySample> MemorySamples,
    IReadOnlyDictionary<string, long> FinalMetrics,
    IReadOnlyDictionary<string, long> ActiveCallBreakdown,
    ChaosDrainResult? Drain,
    ChaosFailure? TerminalFailure,
    ChaosDiagnosticArtifact? DiagnosticArtifact,
    IReadOnlyDictionary<string, long> Failures,
    IReadOnlyList<string> FailureSamples,
    IReadOnlyList<string> UnobservedTaskExceptionSamples,
    IReadOnlyList<string> ClientErrors,
    IReadOnlyList<string> ServerErrors,
    IReadOnlyList<ChaosServerStopObservation> ServerStops);

internal sealed record ChaosFailure(string Type, string Message, string? Details)
{
    internal static ChaosFailure FromException(Exception exception)
        => new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.ToString());
}

internal sealed record ChaosDiagnosticArtifact(
    string Kind,
    string Path,
    bool Captured,
    string Details);

internal enum ChaosTransport
{
    Tcp,
    SharedMemory
}

internal sealed record MemorySample(
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    long RetainedBytes,
    long ProcessWorkingSetBytes,
    long ProcessPrivateBytes,
    long GcHeapSizeBytes,
    long GcTotalCommittedBytes,
    long GcFragmentedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ProcessThreadCount,
    int ThreadPoolThreadCount,
    long ThreadPoolPendingWorkItemCount,
    long ThreadPoolCompletedWorkItemCount,
    int DispatcherRetainedCount,
    long UnobservedTaskExceptions);
