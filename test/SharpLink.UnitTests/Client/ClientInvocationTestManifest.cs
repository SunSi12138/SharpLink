using System.Reflection;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

/// <summary>Explicit local identity for the low-level client lifecycle fixtures' contract 1.</summary>
internal sealed class ClientInvocationTestManifest : ISharpLinkGeneratedAssemblyManifest
{
    internal static readonly ClientInvocationTestManifest Instance = new();
    internal static readonly RpcHash128 Hash = new(0x636c69656e742d31UL, 0x746573742d727063UL);
    internal static readonly Guid ServerInstanceId = new("41529994-0da9-43be-b061-9dbdcc90bf68");
    internal static readonly Guid RegistryEpoch = new("ce1e865c-d89d-4b11-bbba-2e7d41a97608");
    internal static readonly KeyValuePair<long, RpcHash128>[] ContractsOnWire = [new(1, Hash)];

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public Assembly OwnerAssembly => typeof(ClientInvocationTestManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => Hash;
    public string CompileTimeDescriptor => "client-invocation-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
    [
        new(typeof(IInvocationContract), typeof(IInvocationContract).FullName!, 1,
            "0101010101010101010101010101010101010101010101010101010101010101", [],
            static (channel, _) => new InvocationProxy(channel), static _ => throw new NotSupportedException())
    ];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];

    internal interface IInvocationContract : IService;
    internal sealed class InvocationProxy(IRpcChannel channel) : IInvocationContract
    {
        internal IRpcChannel Channel { get; } = channel;
    }
}
