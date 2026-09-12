using SharpLink.Abstractions;
using SharpLink.ReferencedCodecProvider;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ModuleDependencyConsumer.ModuleDependencyManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ModuleDependencyConsumer;

public sealed class ModuleDependencyManifest : ISharpLinkGeneratedAssemblyManifest
{
    private static readonly IReadOnlyList<string> ModuleDependencies =
        new[] { typeof(Payload).Assembly.FullName! };

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ModuleDependencyManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x4d6f64756c654465UL, 0x70656e64656e6379UL);
    public string CompileTimeDescriptor => "module-dependency-consumer";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => ModuleDependencies;
}
