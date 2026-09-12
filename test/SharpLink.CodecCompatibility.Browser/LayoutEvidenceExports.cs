using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace SharpLink.CodecCompatibility;

[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    [JSExport]
    public static string LayoutProduce(string profile, string sharpLinkCommit, string sdkVersion)
        => LayoutEvidenceProbe.ProduceJson(
            sharpLinkCommit,
            sdkVersion,
            "net10.0/browser-wasm",
            profile,
            executionEnvironmentOverride: "browser");

    [JSExport]
    public static string LayoutVerify(string envelopesJson, string sharpLinkCommit, string sdkVersion)
        => LayoutEvidenceProbe.VerifyJson(
            envelopesJson,
            sharpLinkCommit,
            sdkVersion,
            "net10.0/browser-wasm",
            executionEnvironmentOverride: "browser");
}
