using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.LoadTestBase;

/// <summary>Writes machine-readable load-test evidence with enough environment data for A/B comparison.</summary>
public static class PerformanceReportWriter
{
    /// <summary>Writes one completed workload report when <paramref name="path"/> is configured.</summary>
    public static void Write<TConfiguration, TResult>(
        string? path,
        string workload,
        TConfiguration configuration,
        IReadOnlyList<TResult> results,
        JsonSerializerContext serializerContext)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var report = new PerformanceReport<TConfiguration, TResult>(
            2,
            workload,
            DateTimeOffset.UtcNow,
            ReadCommit(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            configuration,
            results);

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, report.GetType(), serializerContext));
        Console.WriteLine($"[Evidence] JSON report: {fullPath}");
    }

    private static string ReadCommit()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ??
                         Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return "unknown";
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2_000) || process.ExitCode != 0)
                return "unknown";
            return output.Trim();
        }
        catch
        {
            return "unknown";
        }
    }
}

/// <summary>Machine-readable performance evidence emitted by a load-test executable.</summary>
public sealed record PerformanceReport<TConfiguration, TResult>(
    int SchemaVersion,
    string Workload,
    DateTimeOffset TimestampUtc,
    string Commit,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string Runtime,
    int ProcessorCount,
    bool ServerGc,
    string GcLatencyMode,
    string? AssemblyVersion,
    TConfiguration Configuration,
    IReadOnlyList<TResult> Results);
