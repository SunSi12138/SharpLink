using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Foundation;
using UIKit;

namespace SharpLink.CodecCompatibility;

public static class Application
{
    public static void Main(string[] args)
        => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = new ProbeViewController()
        };
        Window.MakeKeyAndVisible();
        return true;
    }
}

internal sealed class ProbeViewController : UIViewController
{
    private UILabel? _status;
    private bool _started;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;
        _status = new UILabel(View.Bounds)
        {
            Text = "SharpLink codec compatibility probe",
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };
        View.AddSubview(_status);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (_started)
            return;
        _started = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var mode = Environment.GetEnvironmentVariable("SHARPLINK_MODE") ?? "produce";
            var endpoint = Environment.GetEnvironmentVariable("SHARPLINK_ENDPOINT")
                ?? throw new InvalidOperationException("Missing SHARPLINK_ENDPOINT.");
            var commit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown";
            var sdk = Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown";
            var targetFramework = Environment.GetEnvironmentVariable("SHARPLINK_TARGET_FRAMEWORK")
                ?? "net10.0-ios/iossimulator";

            string result;
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            if (string.Equals(mode, "produce", StringComparison.Ordinal))
            {
                result = PortableProbe.ProduceJson(
                    commit,
                    sdk,
                    targetFramework,
                    runtimeFamilyOverride: "Mono",
                    executionEnvironmentOverride: "simulator");
            }
            else if (string.Equals(mode, "verify", StringComparison.Ordinal))
            {
                var input = await client.GetStringAsync($"{endpoint}/input.json");
                result = PortableProbe.VerifyJson(
                    input,
                    commit,
                    sdk,
                    targetFramework,
                    runtimeFamilyOverride: "Mono",
                    executionEnvironmentOverride: "simulator");
            }
            else
            {
                throw new InvalidOperationException($"Unknown iOS probe mode: {mode}.");
            }

            using var content = new StringContent(result, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{endpoint}/result", content);
            response.EnsureSuccessStatusCode();
            if (_status is not null)
                _status.Text = "completed";
        }
        catch (Exception exception)
        {
            if (_status is not null)
                _status.Text = exception.ToString();
            try
            {
                var endpoint = Environment.GetEnvironmentVariable("SHARPLINK_ENDPOINT");
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var message = exception.ToString().Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal)
                        .Replace("\r", "\\r", StringComparison.Ordinal)
                        .Replace("\n", "\\n", StringComparison.Ordinal);
                    using var content = new StringContent(
                        $"{{\"portableProbeError\":\"{message}\"}}",
                        Encoding.UTF8,
                        "application/json");
                    await client.PostAsync($"{endpoint}/result", content);
                }
            }
            catch
            {
            }
        }
    }
}
