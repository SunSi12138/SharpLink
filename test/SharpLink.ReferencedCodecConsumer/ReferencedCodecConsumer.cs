using SharpLink.Abstractions;
using SharpLink.ReferencedCodecProvider;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ReferencedCodecConsumer.ConsumerManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ReferencedCodecConsumer;

public sealed class ConsumerManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
{
    private static readonly IReadOnlyList<SharpLinkReferencedCodecDependency> Referenced =
        new SharpLinkReferencedCodecDependency[]
        {
            new(
                typeof(Payload),
                new RpcHash128(ProviderManifest.CodecHashHigh, ProviderManifest.CodecHashLow))
        };

    public ConsumerManifest() { }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ConsumerManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x1234567890ABCDEFUL, 0xFEDCBA0987654321UL);
    public string CompileTimeDescriptor => "referenced-codec-consumer";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies => Referenced;
}
