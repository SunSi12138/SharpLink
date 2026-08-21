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

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var controller = new ProbeViewController();
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = controller
        };
        Window.MakeKeyAndVisible();

        Console.WriteLine("SharpLink codec compatibility iOS host launched.");
        _ = RunProbeAsync(controller);
        return true;
    }

    private static async Task RunProbeAsync(ProbeViewController controller)
    {
        try
        {
            await Task.Yield();

            var mode = Environment.GetEnvironmentVariable("SHARPLINK_MODE") ?? "produce";
            var endpoint = Environment.GetEnvironmentVariable("SHARPLINK_ENDPOINT")
                ?? throw new InvalidOperationException("Missing SHARPLINK_ENDPOINT.");
            var commit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown";
            var sdk = Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown";
            var targetFramework = Environment.GetEnvironmentVariable("SHARPLINK_TARGET_FRAMEWORK")
                ?? "net10.0-ios/iossimulator";

            Console.WriteLine($"SharpLink codec probe starting: mode={mode}, target={targetFramework}.");
            controller.SetStatus($"running {mode}");

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

            Console.WriteLine($"SharpLink codec probe generated {Encoding.UTF8.GetByteCount(result)} result bytes; posting to host.");
            using var content = new StringContent(result, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{endpoint}/result", content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("SharpLink codec compatibility iOS probe completed.");
            controller.SetStatus("completed");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SharpLink codec compatibility iOS probe failed: {exception}");
            controller.SetStatus(exception.ToString());
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
            catch (Exception reportingException)
            {
                Console.Error.WriteLine($"Failed to report iOS probe error to host: {reportingException}");
            }
        }
    }
}

internal sealed class ProbeViewController : UIViewController
{
    private UILabel? _status;
    private string _statusText = "SharpLink codec compatibility probe";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;
        _status = new UILabel(View.Bounds)
        {
            Text = _statusText,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
        };
        View.AddSubview(_status);
    }

    public void SetStatus(string text)
    {
        _statusText = text;
        if (_status is not null)
            _status.Text = text;
    }
}
