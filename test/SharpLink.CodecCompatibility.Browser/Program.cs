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
#if SHARPLINK_BROWSER_CORECLR
    private const string TargetFrameworkIdentity = "net11.0/browser-wasm";
    private const string? ExpectedCompilationMode = null;
    private const string? ExpectedRuntimeFamily = "CoreCLR";
#else
    private const string TargetFrameworkIdentity = "net10.0/browser-wasm";
    private const string? ExpectedCompilationMode = "Interpreter";
    private const string? ExpectedRuntimeFamily = null;
#endif

    [JSExport]
    public static string Produce(string sharpLinkCommit, string sdkVersion)
        => PortableProbe.ProduceJson(
            sharpLinkCommit,
            sdkVersion,
            TargetFrameworkIdentity,
            expectedCompilationMode: ExpectedCompilationMode,
            expectedRuntimeFamily: ExpectedRuntimeFamily,
            executionEnvironmentOverride: "browser");

    [JSExport]
    public static string Verify(string envelopesJson, string sharpLinkCommit, string sdkVersion)
        => PortableProbe.VerifyJson(
            envelopesJson,
            sharpLinkCommit,
            sdkVersion,
            TargetFrameworkIdentity,
            expectedCompilationMode: ExpectedCompilationMode,
            expectedRuntimeFamily: ExpectedRuntimeFamily,
            executionEnvironmentOverride: "browser");
}
