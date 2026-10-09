using System;
using System.IO;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;

namespace SharpLink.Profiling753;

// Diagnostic-only boundary events; never linked into shipping Runtime/Client/Server.
[EventSource(Name = "SharpLink-753-ProfileWindow")]
internal sealed class ProfileWindow753 : EventSource
{
    internal static readonly ProfileWindow753 Log = new();
    internal static readonly string RunId = Environment.GetEnvironmentVariable("SHARPLINK_PROFILE_RUN_ID")
        ?? throw new InvalidOperationException("SHARPLINK_PROFILE_RUN_ID is required for profiling copies.");
    private static readonly string MetadataPath = Environment.GetEnvironmentVariable("SHARPLINK_PROFILE_METADATA")
        ?? throw new InvalidOperationException("SHARPLINK_PROFILE_METADATA is required for profiling copies.");
    private static long s_begin, s_started, s_stopped;
    private static bool s_begun, s_closed, s_ended;
    private static string? s_workload;
    private ProfileWindow753() { }
    [NonEvent]
    internal static void Initialize() { _ = Log; _ = RunId; _ = MetadataPath; }
    [NonEvent]
    internal static void Begin(string workload)
    {
        if (s_begun) throw new InvalidOperationException("Duplicate profile begin.");
        s_begun = true; s_workload = workload; s_begin = Stopwatch.GetTimestamp();
        Log.WindowBegin(workload, RunId, s_begin);
    }
    [NonEvent]
    internal static void Close(string workload, long startedTicks, long stoppedTicks)
    {
        if (!s_begun || s_closed || workload != s_workload || startedTicks < s_begin || stoppedTicks <= startedTicks)
            throw new InvalidOperationException("Invalid profile admission boundary.");
        s_closed = true; s_started = startedTicks; s_stopped = stoppedTicks;
        Log.AdmissionsClosed(workload, RunId, startedTicks, stoppedTicks);
    }
    [NonEvent]
    internal static void End(string workload, long operations)
    {
        var end = Stopwatch.GetTimestamp();
        if (!s_closed || s_ended || workload != s_workload || end < s_stopped || operations <= 0)
            throw new InvalidOperationException("Invalid profile end.");
        s_ended = true;
        Log.WindowEnd(workload, RunId, end, operations);
        // Serialize only AFTER the final evidence snapshot/end event, including untraced controls.
        using var stream = new FileStream(MetadataPath, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, new {
            schemaVersion = 1, diagnosticOnly = true, workload, runId = RunId,
            processId = Environment.ProcessId, runtimeVersion = Environment.Version.ToString(),
            stopwatchFrequency = Stopwatch.Frequency, beginTicks = s_begin,
            startedTicks = s_started, stoppedTicks = s_stopped, endTicks = end, operations
        }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static class Keywords { public const EventKeywords Window = (EventKeywords)1; }

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Window)]
    public void WindowBegin(string workload, string runId, long boundaryTicks)
    {
        if (IsEnabled()) WriteEvent(1, workload, runId, boundaryTicks);
    }
    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Window)]
    public void AdmissionsClosed(string workload, string runId, long startedTicks, long stoppedTicks)
    {
        if (IsEnabled()) WriteEvent(2, workload, runId, startedTicks, stoppedTicks);
    }
    [Event(3, Level = EventLevel.Informational, Keywords = Keywords.Window)]
    public void WindowEnd(string workload, string runId, long boundaryTicks, long operations)
    {
        if (IsEnabled()) WriteEvent(3, workload, runId, boundaryTicks, operations);
    }
}
