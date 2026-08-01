using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly("orders", typeof(MultiCluster.Orders.Contracts.IOrdersService))]

namespace MultiCluster.Orders.Contracts;

[RpcContract]
public interface IOrdersService : IService
{
    ValueTask<string> GetClusterAsync(CancellationToken cancellationToken);
}
