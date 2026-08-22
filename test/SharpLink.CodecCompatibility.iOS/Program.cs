using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Foundation;
using UIKit;

namespace SharpLink.CodecCompatibility;

public static class Application
{
    private const string InputFileName = "sharplink-input.json";
    private const string ResultFileName = "sharplink-result.json";

    public static void Main(string[] args)
    {
        RunProbe();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    private static void RunProbe()
    {
        string? resultPath = null;
        try
        {
            var documentsDirectory = GetDocumentsDirectory();
            Directory.CreateDirectory(documentsDirectory);
            var inputPath = Path.Combine(documentsDirectory, InputFileName);
            resultPath = Path.Combine(documentsDirectory, ResultFileName);

            var mode = Environment.GetEnvironmentVariable("SHARPLINK_MODE") ?? "produce";
            var commit = Environment.GetEnvironmentVariable("SHARPLINK_COMMIT") ?? "unknown";
            var sdk = Environment.GetEnvironmentVariable("SHARPLINK_SDK_VERSION") ?? "unknown";
            var targetFramework = Environment.GetEnvironmentVariable("SHARPLINK_TARGET_FRAMEWORK")
                ?? "net10.0-ios/iossimulator";

            Console.WriteLine($"SharpLink codec probe starting from Main: mode={mode}, target={targetFramework}.");

            string result;
            if (string.Equals(mode, "produce", StringComparison.Ordinal))
            {
                result = PortableProbe.ProduceJson(
                    commit,
                    sdk,
                    targetFramework,
                    expectedCompilationMode: "Interpreter",
                    expectedRuntimeFamily: "Mono",
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
                    expectedCompilationMode: "Interpreter",
                    expectedRuntimeFamily: "Mono",
                    executionEnvironmentOverride: "simulator");
            }
            else
            {
                throw new InvalidOperationException($"Unknown iOS probe mode: {mode}.");
            }

            WriteResultAtomically(resultPath, result);
            Console.WriteLine($"SharpLink codec probe completed from Main; persisted {Encoding.UTF8.GetByteCount(result)} result bytes.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SharpLink codec compatibility iOS probe failed in Main: {exception}");
            try
            {
                var documentsDirectory = GetDocumentsDirectory();
                Directory.CreateDirectory(documentsDirectory);
                resultPath ??= Path.Combine(documentsDirectory, ResultFileName);
                var json = JsonSerializer.Serialize(new
                {
                    portableProbeError = exception.ToString()
                });
                WriteResultAtomically(resultPath, json);
            }
            catch (Exception reportingException)
            {
                Console.Error.WriteLine($"Failed to persist iOS probe error: {reportingException}");
            }
        }
    }

    private static void WriteResultAtomically(string resultPath, string contents)
    {
        var temporaryPath = resultPath + ".tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string GetDocumentsDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, "Documents");

        var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsDirectory))
            throw new InvalidOperationException("iOS Documents directory is unavailable.");
        return documentsDirectory;
    }
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = controller
        };
        Window.MakeKeyAndVisible();
        return true;
    }
}
