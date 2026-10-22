using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Isolates only the two steady-state zero-allocation oracles. Unrelated assembly unloads
/// flush CoreCLR's shared cast cache; the standalone host also bounds its natural startup
/// growth before invoking the unchanged measurement once, with default tiering and context flow.
/// Ownership, cold-path, context and concurrent-unload behavior tests remain in this process.
/// </summary>
internal static class ReadOwnershipAllocationProbe
{
    private const string SuccessMarker = "READ_OWNERSHIP_ALLOCATION_PASSED";
    private const string FailureMarker = "READ_OWNERSHIP_ALLOCATION_FAILED";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(string scenario, CancellationToken cancellationToken)
    {
        var assembly = typeof(ReadOwnershipPipeReaderLifetimeTests).Assembly;
        var mvid = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var results = Path.Combine(Path.GetTempPath(), "sharplink-read-allocation", nonce);
        var resultPath = Path.Combine(results, "result.json");
        Directory.CreateDirectory(results);
        var probe = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "allocation-probe",
            "SharpLink.ReadOwnershipAllocationProbe.dll");
        Ensure(File.Exists(probe), $"The standalone allocation probe is missing: {probe}");
        var start = new ProcessStartInfo
        {
            FileName = GetDotNetHost(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(Environment.Version.ToString());
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(assembly.Location);
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(mvid);
        start.ArgumentList.Add(resultPath);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Keep startup + measurement inside the original assembly-wide 30-second limit.
        // The larger outer test timeout only reserves process launch/cleanup/reporting time.
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start allocation probe.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        var timedOut = false;
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (child.HasExited) { }
                await child.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
        }
        var output = await stdout.WaitAsync(CleanupTimeout).ConfigureAwait(false);
        var errors = await stderr.WaitAsync(CleanupTimeout).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(results, "stdout.log"), output);
        File.WriteAllText(Path.Combine(results, "stderr.log"), errors);
        var expected = $"{SuccessMarker}|case={scenario}|nonce={nonce}|mvid={mvid}|runtime={Environment.Version}|architecture={RuntimeInformation.ProcessArchitecture}|executed=1|warmup=1000|measured=10000|allocated=0";
        var exactMatches = 0;
        var successLines = 0;
        using (var lines = new StringReader(output))
        {
            while (lines.ReadLine() is { } line)
            {
                if (string.Equals(line, expected, StringComparison.Ordinal))
                    exactMatches++;
                if (line.StartsWith(SuccessMarker + "|", StringComparison.Ordinal))
                    successLines++;
            }
        }
        Ensure(!timedOut && child.ExitCode == 0 && exactMatches == 1 && successLines == 1 &&
            !output.Contains(FailureMarker, StringComparison.Ordinal),
            $"Allocation probe '{scenario}' failed (timeout={timedOut}, exit={child.ExitCode}). " +
            $"Child results: {results}\nstdout:\n{output}\nstderr:\n{errors}");
        var verifiedReport = ValidateExecutionReport(resultPath, scenario, nonce, mvid);
        // Retain compact, validated child provenance in the ordinary test output/report.
        // This occurs only after the child's one measurement and every parent assertion.
        Console.WriteLine($"READ_OWNERSHIP_ALLOCATION_VERIFIED|{verifiedReport}");
        // Failed child output/reports remain available in the parent failure and private path.
        Directory.Delete(results, recursive: true);
    }

    private static string GetDotNetHost()
    {
        var process = Environment.ProcessPath;
        if (process is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            return process;
        // Framework-dependent test apphosts use the same installed runtime root as CoreLib.
        var root = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        var host = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Ensure(host is not null && File.Exists(host), "Could not locate the current runtime's dotnet host.");
        return host!;
    }

    private static string ValidateExecutionReport(string path, string scenario, string nonce, string mvid)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        var result = report.RootElement;
        Ensure(result.GetProperty("scenario").GetString() == scenario &&
            result.GetProperty("nonce").GetString() == nonce &&
            result.GetProperty("assemblyMvid").GetString() == mvid &&
            result.GetProperty("runtime").GetString() == Environment.Version.ToString() &&
            result.GetProperty("architecture").GetString() == RuntimeInformation.ProcessArchitecture.ToString() &&
            result.GetProperty("executed").GetInt32() == 1 && result.GetProperty("passed").GetBoolean() &&
            result.GetProperty("warmup").GetInt32() == 1000 && result.GetProperty("measured").GetInt32() == 10000 &&
            result.GetProperty("allocated").GetInt64() == 0 && result.GetProperty("cacheEntries").GetInt32() == 4096 &&
            result.GetProperty("primedTypes").GetInt32() is >= 0 and <= 20_000 &&
            result.GetProperty("cacheUnchanged").GetBoolean() &&
            result.GetProperty("nonDefaultContext").GetBoolean() && result.GetProperty("contextFlowEnabled").GetBoolean(),
            $"Allocation child must execute exactly the requested zero-allocation case '{scenario}'. Report: {path}");
        return result.GetRawText();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
