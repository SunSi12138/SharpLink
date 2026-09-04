using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Widget;

namespace SharpLink.CodecCompatibility;

[Activity(
    Name = "com.sharplink.codeccompat.LayoutEvidenceActivity",
    Label = "SharpLink UnsafeBlit Layout Evidence",
    Exported = true)]
public sealed class LayoutEvidenceActivity : Activity
{
    private const string LogTag = "SharpLinkLayoutEvidence";
    private const string InputFileName = "sharplink-input.json";
    private const string ResultFileName = "sharplink-result.json";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var status = new TextView(this) { Text = "SharpLink UnsafeBlit layout evidence" };
        SetContentView(status);
        _ = RunAsync(status);
    }

    private async Task RunAsync(TextView status)
    {
        string? resultPath = null;
        try
        {
            await Task.Yield();
            var filesDirectory = FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Android app files directory is unavailable.");
            Directory.CreateDirectory(filesDirectory);
            var inputPath = Path.Combine(filesDirectory, InputFileName);
            resultPath = Path.Combine(filesDirectory, ResultFileName);
            var mode = Intent?.GetStringExtra("mode") ?? "layout-produce";
            var profile = Intent?.GetStringExtra("profile") ?? LayoutEvidenceProfiles.FixedWidth;
            var commit = Intent?.GetStringExtra("commit") ?? "unknown";
            var sdk = Intent?.GetStringExtra("sdk") ?? "unknown";
            var expectedRuntimeFamily = Intent?.GetStringExtra("runtimeFamily") ?? "unknown";
            var rid = DetectRuntimeIdentifier();
            var targetFramework = $"net10.0-android/{rid}";
            const string executionEnvironment = "emulator";
            Log.Info(LogTag, $"starting mode={mode} profile={profile} expectedRuntime={expectedRuntimeFamily} rid={rid}");
            status.Text = mode;

            string result;
            if (string.Equals(mode, "layout-produce", StringComparison.Ordinal))
            {
                result = LayoutEvidenceProbe.ProduceJson(commit, sdk, targetFramework, profile, expectedRuntimeFamily, executionEnvironment);
            }
            else if (string.Equals(mode, "layout-verify", StringComparison.Ordinal))
            {
                result = LayoutEvidenceProbe.VerifyJson(File.ReadAllText(inputPath, Encoding.UTF8), commit, sdk, targetFramework, expectedRuntimeFamily, executionEnvironment);
            }
            else
            {
                throw new InvalidOperationException($"Unknown Android layout evidence mode: {mode}.");
            }

            File.WriteAllText(resultPath, result, new UTF8Encoding(false));
            Log.Info(LogTag, $"completed bytes={Encoding.UTF8.GetByteCount(result)}");
            status.Text = "completed";
        }
        catch (Exception exception)
        {
            Log.Error(LogTag, exception.ToString());
            status.Text = exception.ToString();
            try
            {
                var filesDirectory = FilesDir?.AbsolutePath;
                if (!string.IsNullOrWhiteSpace(filesDirectory))
                {
                    resultPath ??= Path.Combine(filesDirectory, ResultFileName);
                    File.WriteAllText(resultPath, JsonSerializer.Serialize(new { portableProbeError = exception.ToString() }), new UTF8Encoding(false));
                }
            }
            catch (Exception reportingException)
            {
                Log.Error(LogTag, $"failed to persist layout evidence error: {reportingException}");
            }
        }
    }

    private static string DetectRuntimeIdentifier()
    {
        var reported = RuntimeInformation.RuntimeIdentifier;
        if (reported.StartsWith("android-", StringComparison.OrdinalIgnoreCase))
            return reported;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var observed => throw new InvalidOperationException($"Unsupported Android process architecture: {observed}.")
        };
        return $"android-{architecture}";
    }
}
