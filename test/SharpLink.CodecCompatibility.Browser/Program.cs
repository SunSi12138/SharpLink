using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace SharpLink.CodecCompatibility;

internal static class Program
{
    private static void Main()
    {
    }
}

[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    [JSExport]
    public static string Produce(string sharpLinkCommit, string sdkVersion)
        => PortableProbe.ProduceJson(
            sharpLinkCommit,
            sdkVersion,
            "net10.0/browser-wasm",
            compilationModeOverride: "Interpreter",
            expectedRuntimeFamily: "Mono",
            executionEnvironmentOverride: "browser");

    [JSExport]
    public static string Verify(string envelopesJson, string sharpLinkCommit, string sdkVersion)
        => PortableProbe.VerifyJson(
            envelopesJson,
            sharpLinkCommit,
            sdkVersion,
            "net10.0/browser-wasm",
            compilationModeOverride: "Interpreter",
            expectedRuntimeFamily: "Mono",
            executionEnvironmentOverride: "browser");
}
