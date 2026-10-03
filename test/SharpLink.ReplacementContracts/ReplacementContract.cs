using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

// A real generated second assembly identity: the original contract's ID is retained while
// adding a contract changes the owning RPC assembly hash. No injected fake hash is involved.
[RpcContract]
public interface IReplacementIdentityProbe : IService
{
    ValueTask<int> ProbeAsync(int value, CancellationToken cancellationToken);
}
