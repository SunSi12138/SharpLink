using System.Reflection;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Explicit local identity for manual-channel tests against the simulated wire peer.
// It is not registered in the process-wide generated catalog and has no server Stub.
internal sealed class ClientInvocationTestManifest : ISharpLinkGeneratedAssemblyManifest
{
    internal static readonly ClientInvocationTestManifest Instance = new();
    internal static readonly RpcHash128 Hash = new(0x746573742d727063UL, 0x2d62696e64696e67UL);
    internal static readonly IReadOnlyList<KeyValuePair<long, RpcHash128>> RemoteContracts =
        [new(1, Hash)];
    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "manual-channel-test";
    public Assembly OwnerAssembly => typeof(ClientInvocationTestManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => Hash;
    public string CompileTimeDescriptor => "manual-channel-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
    [new(typeof(IInvocationTestContract), nameof(IInvocationTestContract), 1, new string('c', 64), [],
        static (_, _) => throw new NotSupportedException(), static _ => throw new NotSupportedException())];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];

    internal interface IInvocationTestContract : IService;
}
