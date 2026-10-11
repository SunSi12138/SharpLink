using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

/// <summary>Explicit identity for transport/deadline fixtures whose fake peer advertises contract 1.</summary>
internal static class ClientContractTestBinding
{
    private static readonly ContractRequirement Requirement = new(1, new RpcHash128(0x6d756c7469636c75UL, 0x737465722d763031UL));
    internal static IRpcChannel Bind(SharpLinkClient client) => new SharpLinkContractRpcChannel(client, Requirement);
}
