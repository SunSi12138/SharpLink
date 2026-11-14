using System.Diagnostics;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static void CaptureAlcFailure()
    {
        if (Environment.GetEnvironmentVariable("SHARPLINK_CAPTURE_ALC_DUMP") != "1") return;
        try
        {
            // Failure-only hosted diagnostics. Keep the raw heap OUTSIDE the uploaded evidence
            // directory; only targeted type/root text is retained by the Action.
            var output = Path.GetFullPath("artifacts/775-diagnostics/alc-failure.dmp");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var tool = Path.GetFullPath("artifacts/775-tools/" + (OperatingSystem.IsWindows() ? "dotnet-dump.exe" : "dotnet-dump"));
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "collect", "--process-id", Environment.ProcessId.ToString(), "--type", "Full", "--output", output })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Diagnostic helper did not start.");
            if (!process.WaitForExit(45000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Failure dump exceeded 45 seconds."); }
            Console.WriteLine($"ALC_DIAGNOSTIC collect exit={process.ExitCode}");
        }
        catch (Exception failure) { Console.WriteLine("ALC_DIAGNOSTIC " + failure); }
    }
}

