using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Widget;

namespace SharpLink.CodecCompatibility;

[Activity(
    Name = "com.sharplink.codeccompat.MainActivity",
    Label = "SharpLink Codec Compatibility",
    MainLauncher = true,
    Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var status = new TextView(this) { Text = "SharpLink codec compatibility probe" };
        SetContentView(status);
        _ = RunAsync(status);
    }

    private async Task RunAsync(TextView status)
    {
        try
        {
            var mode = Intent?.GetStringExtra("mode") ?? "produce";
            var endpoint = Intent?.GetStringExtra("endpoint")
                ?? throw new InvalidOperationException("Missing endpoint intent extra.");
            var commit = Intent?.GetStringExtra("commit") ?? "unknown";
            var sdk = Intent?.GetStringExtra("sdk") ?? "unknown";
            var runtimeFamily = Intent?.GetStringExtra("runtimeFamily") ?? "unknown";

            string result;
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            if (string.Equals(mode, "produce", StringComparison.Ordinal))
            {
                result = PortableProbe.ProduceJson(
                    commit,
                    sdk,
                    "net10.0-android/android-x64",
                    runtimeFamilyOverride: runtimeFamily,
                    executionEnvironmentOverride: "emulator");
            }
            else if (string.Equals(mode, "verify", StringComparison.Ordinal))
            {
                var input = await client.GetStringAsync($"{endpoint}/input.json");
                result = PortableProbe.VerifyJson(
                    input,
                    commit,
                    sdk,
                    "net10.0-android/android-x64",
                    runtimeFamilyOverride: runtimeFamily,
                    executionEnvironmentOverride: "emulator");
            }
            else
            {
                throw new InvalidOperationException($"Unknown Android probe mode: {mode}.");
            }

            using var content = new StringContent(result, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{endpoint}/result", content);
            response.EnsureSuccessStatusCode();
            status.Text = "completed";
        }
        catch (Exception exception)
        {
            status.Text = exception.ToString();
            try
            {
                var endpoint = Intent?.GetStringExtra("endpoint");
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        portableProbeError = exception.ToString()
                    });
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync($"{endpoint}/result", content);
                }
            }
            catch
            {
            }
        }
    }
}
