using System.Reflection;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifest(typeof(SharpLink.CodecPublicationProbe.PublicationProbeManifest), SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol, "publication-regression", SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.CodecPublicationProbe;

public sealed class PublicationProbeManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
{
    // Deterministic reentrancy through the public metadata SPI; returned metadata never changes.
    public static Action? OnRead;
    private static readonly SharpLinkReferencedCodecDependency[] Empty = [];
    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "publication-regression";
    public Assembly OwnerAssembly => typeof(PublicationProbeManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x101, 0x202);
    public string CompileTimeDescriptor => "publication-regression-trigger";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies
    {
        get
        {
            Interlocked.Exchange(ref OnRead, null)?.Invoke();
            return Empty;
        }
    }
}
