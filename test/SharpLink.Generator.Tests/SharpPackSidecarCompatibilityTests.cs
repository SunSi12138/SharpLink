using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void SharpPackSidecarWireShapeShouldAffectCodecHash()
    {
        var firstVendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalRequest
{
    public int Id { get; set; }
}
""");
        var secondVendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalRequest
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
""");
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var first = RunSharpPackAndCompile("SharpPackCompatibilityA", source, [firstVendor]);
        var second = RunSharpPackAndCompile("SharpPackCompatibilityB", source, [secondVendor]);
        EnsureNoSharpPackErrors(first);
        EnsureNoSharpPackErrors(second);

        var firstHash = GetSharpPackCodecHash(first.DriverRunResult, "global::Vendor.ExternalRequest");
        var secondHash = GetSharpPackCodecHash(second.DriverRunResult, "global::Vendor.ExternalRequest");
        Ensure(!string.Equals(firstHash, secondHash, StringComparison.Ordinal),
            "sidecar wire-shape changes must change the negotiated CodecHash");
    }

    private static string GetSharpPackCodecHash(
        GeneratorDriverRunResult result,
        string targetType)
    {
        var generated = result.Results
            .SelectMany(static item => item.GeneratedSources)
            .Single(static item => item.HintName == "SharpLink.GeneratedCodecs.g.cs")
            .SourceText
            .ToString();
        var targetMarker = $"public Type TargetType => typeof({targetType});";
        var targetIndex = generated.IndexOf(targetMarker, StringComparison.Ordinal);
        if (targetIndex < 0)
            throw new Exception($"Generated SharpPack factory for '{targetType}' was not found.");
        const string hashMarker = "public RpcHash128 CodecHash =>";
        var hashIndex = generated.IndexOf(hashMarker, targetIndex, StringComparison.Ordinal);
        if (hashIndex < 0)
            throw new Exception($"Generated CodecHash for '{targetType}' was not found.");
        var lineEnd = generated.IndexOf('\n', hashIndex);
        return (lineEnd < 0 ? generated[hashIndex..] : generated[hashIndex..lineEnd]).Trim();
    }
}
