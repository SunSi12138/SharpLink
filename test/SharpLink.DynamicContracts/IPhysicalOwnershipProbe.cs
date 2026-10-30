using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IPhysicalOwnershipProbe : IService
{
    [Timeout(0.2)]
    IAsyncEnumerable<int> DeadlineStreamAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<int> CooperativeStreamAsync(CancellationToken cancellationToken);
}
