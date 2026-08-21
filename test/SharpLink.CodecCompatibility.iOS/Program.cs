using System;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private const string InputFileName = "sharplink-input.json";
    private const string ResultFileName = "sharplink-result.json";

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
        string? resultPath = null;
        try
        {
            await Task.Yield();

            var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documentsDirectory))
                throw new InvalidOperationException("iOS Documents directory is unavailable.");
            Directory.CreateDirectory(documentsDirectory);
            var inputPath = Path.Combine(documentsDirectory, InputFileName);
            resultPath = Path.Combine(documentsDirectory, ResultFileName);

            var mode = Environment.GetEnvironmentVariable("SHARPLINK_MODE") ?? "produce";
            var commit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown";
            var sdk = Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown";
            var targetFramework = Environment.GetEnvironmentVariable("SHARPLINK_TARGET_FRAMEWORK")
                ?? "net10.0-ios/iossimulator";

            Console.WriteLine($"SharpLink codec probe starting: mode={mode}, target={targetFramework}.");
            controller.SetStatus($"running {mode}");

            string result;
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
                var input = File.ReadAllText(inputPath, Encoding.UTF8);
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

            File.WriteAllText(resultPath, result, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"SharpLink codec probe completed; persisted {Encoding.UTF8.GetByteCount(result)} result bytes.");
            controller.SetStatus("completed");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SharpLink codec compatibility iOS probe failed: {exception}");
            controller.SetStatus(exception.ToString());
            try
            {
                var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(documentsDirectory))
                {
                    Directory.CreateDirectory(documentsDirectory);
                    resultPath ??= Path.Combine(documentsDirectory, ResultFileName);
                    var json = JsonSerializer.Serialize(new
                    {
                        portableProbeError = exception.ToString()
                    });
                    File.WriteAllText(resultPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch (Exception reportingException)
            {
                Console.Error.WriteLine($"Failed to persist iOS probe error: {reportingException}");
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
