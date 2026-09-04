using System.Linq;
using System.Text.Json.Nodes;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void AssemblyLogicalIdentityChangeShouldFailContractBaseline()
    {
        var source = SimpleContract("ValueTask<int> Echo(int value);");
        var baseline = RunContractGenerator(source);
        Ensure(!baseline.Diagnostics.Any(IsCompatibilityDiagnostic),
            "baseline assembly identity fixture should generate without compatibility diagnostics");

        var root = JsonNode.Parse(baseline.Json)!.AsObject();
        Ensure(root["assemblyLogicalIdentity"]?.GetValue<string>() == "ContractManifestTestAssembly",
            "contract manifest must persist the same logical assembly identity used by RpcAssemblyHash");

        var changedAssemblyBaseline = RewriteManifest(
            baseline.Json,
            manifest => manifest["assemblyLogicalIdentity"] = "Other.Contracts");
        var current = RunContractGenerator(source, changedAssemblyBaseline);
        Ensure(current.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            "changing only the baseline assembly logical identity must require SHARPLINK030");
    }
}
