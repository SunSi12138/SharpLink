using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.ChaosTests;

public static partial class Program
{
    private static string GetCommit()
    {
        try
        {
            var info = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null)
                return "unknown";
            var result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? result : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool? GetWorkingTreeDirty()
    {
        try
        {
            var info = new ProcessStartInfo("git", "status --porcelain")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var result = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return process.ExitCode == 0 ? !string.IsNullOrEmpty(result) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ChaosDiagnosticArtifact> CaptureProcessDumpAsync(string? reportPath)
    {
        var dumpPath = string.IsNullOrWhiteSpace(reportPath)
            ? Path.GetFullPath($"artifacts/chaos/chaos-failure-{Environment.ProcessId}.dmp")
            : Path.ChangeExtension(Path.GetFullPath(reportPath), ".dmp");
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
        var executableName = OperatingSystem.IsWindows() ? "createdump.exe" : "createdump";
        var externalTool = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("SHARPLINK_CHAOS_DUMP_TOOL")
            : null;
        var toolPath = externalTool ?? Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            executableName);
        if (!File.Exists(toolPath))
        {
            return new ChaosDiagnosticArtifact(
                "ProcessDump",
                dumpPath,
                false,
                $"Runtime dump tool was not found at {toolPath}.");
        }

        try
        {
            var info = new ProcessStartInfo(toolPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var processId = Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (externalTool is not null)
            {
                foreach (var argument in new[] { "collect", "--process-id", processId, "--type", "Heap", "--output", dumpPath })
                    info.ArgumentList.Add(argument);
            }
            else
            {
                foreach (var argument in new[] { "--withheap", "--crashreport", "--name", dumpPath, processId })
                    info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info);
            if (process is null)
            {
                return new ChaosDiagnosticArtifact(
                    "ProcessDump", dumpPath, false, "Failed to start the runtime dump tool.");
            }
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                _ = await ReadDumpOutputAsync(standardOutput, standardError).ConfigureAwait(false);
                return new ChaosDiagnosticArtifact(
                    "ProcessDump", dumpPath, false, "Runtime dump capture exceeded 30 seconds.");
            }
            var output = await ReadDumpOutputAsync(standardOutput, standardError).ConfigureAwait(false);
            return new ChaosDiagnosticArtifact(
                "ProcessDump",
                dumpPath,
                process.ExitCode == 0 && File.Exists(dumpPath),
                output);
        }
        catch (Exception exception)
        {
            return new ChaosDiagnosticArtifact(
                "ProcessDump", dumpPath, false, exception.ToString());
        }
    }

    private static async Task<string> ReadDumpOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError)
    {
        try
        {
            var output = ((await standardOutput.ConfigureAwait(false)) + Environment.NewLine +
                          (await standardError.ConfigureAwait(false))).Trim();
            return output.Length > 4096 ? output[..4096] : output;
        }
        catch (Exception exception)
        {
            return $"Failed to read dump-tool output: {exception.Message}";
        }
    }

    private static void WriteReport(string? path, ChaosReport report)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.tmp-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, fullPath, overwrite: true);
        Console.WriteLine($"CHAOS_REPORT {fullPath}");
    }
}
