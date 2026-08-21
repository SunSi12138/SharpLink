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
    Name = "com.sharplink.codeccompat.MainActivity",
    Label = "SharpLink Codec Compatibility",
    MainLauncher = true,
    Exported = true)]
public sealed class MainActivity : Activity
{
    private const string LogTag = "SharpLinkCodecCompat";
    private const string InputFileName = "sharplink-input.json";
    private const string ResultFileName = "sharplink-result.json";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var status = new TextView(this) { Text = "SharpLink codec compatibility probe" };
        SetContentView(status);
        _ = RunAsync(status);
    }

    private async Task RunAsync(TextView status)
    {
        string? resultPath = null;
        try
        {
            await Task.Yield();

            var filesDirectory = FilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Android app files directory is unavailable.");
            Directory.CreateDirectory(filesDirectory);
            var inputPath = Path.Combine(filesDirectory, InputFileName);
            resultPath = Path.Combine(filesDirectory, ResultFileName);

            var mode = Intent?.GetStringExtra("mode") ?? "produce";
            var commit = Intent?.GetStringExtra("commit") ?? "unknown";
            var sdk = Intent?.GetStringExtra("sdk") ?? "unknown";
            var expectedRuntimeFamily = Intent?.GetStringExtra("runtimeFamily") ?? "unknown";
            var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
            if (!runtimeIdentifier.StartsWith("android-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Expected an Android runtime identifier, observed {runtimeIdentifier}.");

            var targetFramework = $"net10.0-android/{runtimeIdentifier}";
            var executionEnvironment = DetectExecutionEnvironment();

            Log.Info(
                LogTag,
                $"probe starting mode={mode} expectedRuntime={expectedRuntimeFamily} rid={runtimeIdentifier} environment={executionEnvironment}");
            status.Text = $"running {mode}";

            string result;
            if (string.Equals(mode, "produce", StringComparison.Ordinal))
            {
                result = PortableProbe.ProduceJson(
                    commit,
                    sdk,
                    targetFramework,
                    expectedRuntimeFamily: expectedRuntimeFamily,
                    executionEnvironmentOverride: executionEnvironment);
            }
            else if (string.Equals(mode, "verify", StringComparison.Ordinal))
            {
                var input = File.ReadAllText(inputPath, Encoding.UTF8);
                result = PortableProbe.VerifyJson(
                    input,
                    commit,
                    sdk,
                    targetFramework,
                    expectedRuntimeFamily: expectedRuntimeFamily,
                    executionEnvironmentOverride: executionEnvironment);
            }
            else
            {
                throw new InvalidOperationException($"Unknown Android probe mode: {mode}.");
            }

            File.WriteAllText(resultPath, result, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log.Info(LogTag, $"probe completed bytes={Encoding.UTF8.GetByteCount(result)}");
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
                    var json = JsonSerializer.Serialize(new
                    {
                        portableProbeError = exception.ToString()
                    });
                    File.WriteAllText(resultPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch (Exception reportingException)
            {
                Log.Error(LogTag, $"failed to persist probe error: {reportingException}");
            }
        }
    }

    private static string DetectExecutionEnvironment()
    {
        var fingerprint = Build.Fingerprint ?? string.Empty;
        var model = Build.Model ?? string.Empty;
        var manufacturer = Build.Manufacturer ?? string.Empty;
        var brand = Build.Brand ?? string.Empty;
        var device = Build.Device ?? string.Empty;
        var product = Build.Product ?? string.Empty;
        var hardware = Build.Hardware ?? string.Empty;

        var isEmulator =
            fingerprint.StartsWith("generic", StringComparison.OrdinalIgnoreCase)
            || fingerprint.Contains("vbox", StringComparison.OrdinalIgnoreCase)
            || model.Contains("google_sdk", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Emulator", StringComparison.OrdinalIgnoreCase)
            || model.Contains("Android SDK built for", StringComparison.OrdinalIgnoreCase)
            || manufacturer.Contains("Genymotion", StringComparison.OrdinalIgnoreCase)
            || (brand.StartsWith("generic", StringComparison.OrdinalIgnoreCase)
                && device.StartsWith("generic", StringComparison.OrdinalIgnoreCase))
            || product.Contains("sdk_google", StringComparison.OrdinalIgnoreCase)
            || product.Contains("google_sdk", StringComparison.OrdinalIgnoreCase)
            || product.Equals("sdk", StringComparison.OrdinalIgnoreCase)
            || product.Contains("sdk_x86", StringComparison.OrdinalIgnoreCase)
            || product.Contains("vbox86p", StringComparison.OrdinalIgnoreCase)
            || product.Contains("emulator", StringComparison.OrdinalIgnoreCase)
            || product.Contains("simulator", StringComparison.OrdinalIgnoreCase)
            || hardware.Contains("goldfish", StringComparison.OrdinalIgnoreCase)
            || hardware.Contains("ranchu", StringComparison.OrdinalIgnoreCase)
            || hardware.Contains("vbox86", StringComparison.OrdinalIgnoreCase);

        return isEmulator ? "emulator" : "physical-device";
    }
}
