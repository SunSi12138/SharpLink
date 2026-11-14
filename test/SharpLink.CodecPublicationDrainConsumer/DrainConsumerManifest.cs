using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.CodecPublicationDrainProvider;

[assembly: SharpLinkGeneratedAssemblyManifest(typeof(SharpLink.CodecPublicationDrainConsumer.DrainConsumerManifest),
    SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol, "test", SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.CodecPublicationDrainConsumer;

public sealed class DrainConsumerManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
{
    private static readonly IReadOnlyList<SharpLinkReferencedCodecDependency> Referenced = CreateDependencies();

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public Assembly OwnerAssembly => typeof(DrainConsumerManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x303, 0x404);
    public string CompileTimeDescriptor => "publication-drain-consumer";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies => Referenced;

    private static IReadOnlyList<SharpLinkReferencedCodecDependency> CreateDependencies()
    {
        var provider = typeof(DrainPayload).Assembly;
        var locator = provider.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()!;
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(locator.ManifestType)!;
        var factory = manifest.Codecs.Single(codec => codec.TargetType == typeof(DrainPayload));
        return Array.AsReadOnly(new[] { new SharpLinkReferencedCodecDependency(typeof(DrainPayload), factory.CodecHash) });
    }
}
