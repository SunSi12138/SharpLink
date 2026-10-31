using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IPhysicalTailOwnershipService : IService
{
    IAsyncEnumerable<int> BlockPhysicalTailAsync(CancellationToken cancellationToken);
}
