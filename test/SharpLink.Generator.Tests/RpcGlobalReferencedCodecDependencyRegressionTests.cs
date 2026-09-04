using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task GlobalOnlyGeneratedCodecShouldPinReferencedChildHash()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var referenced = CreateMetadataReference(
            "ReferencedGlobalPayload",
            """
using System;

[assembly: SharpLink.Abstractions.SharpLinkGeneratedCodecIdentityAttribute(
    typeof(Referenced.Payload),
    0x5151515151515151UL,
    0x6262626262626262UL)]
[assembly: SharpLink.Abstractions.SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(Referenced.Manifest),
    4,
    2,
    "2.0.0-test",
    "sharplink-2.0-api4-rpcchannel-codec-provider-v4")]

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SharpLinkGeneratedCodecIdentityAttribute : Attribute
    {
        public SharpLinkGeneratedCodecIdentityAttribute(Type targetType, ulong high, ulong low) { }
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion,
            string abiIdentity) { }
    }
}

namespace Referenced
{
    public sealed class Payload { public int Value { get; set; } }
    public sealed class Manifest { }
}
""");

        const string source = """
using SharpLink.Sdk;

[RpcSerializable]
public sealed class GlobalHolder
{
    public Referenced.Payload Payload { get; set; } = new();
}
""";

        var manifest = RunGeneratorAndGetSources(source, sdk, referenced)
            .Single(static generated => generated.Contains(
                "ISharpLinkGeneratedAssemblyManifest",
                StringComparison.Ordinal));

        Ensure(
            manifest.Contains("ISharpLinkReferencedCodecDependencyManifest", StringComparison.Ordinal) &&
            manifest.Contains("new SharpLinkReferencedCodecDependency(", StringComparison.Ordinal) &&
            manifest.Contains("typeof(global::Referenced.Payload)", StringComparison.Ordinal) &&
            manifest.Contains("5859553999884210513UL", StringComparison.Ordinal) &&
            manifest.Contains("7089336938131513954UL", StringComparison.Ordinal),
            "a global-only generated Codec must pin the exact referenced child CodecHash used by its declared root CodecHash");
        return Task.CompletedTask;
    }
}
