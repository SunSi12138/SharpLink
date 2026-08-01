using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly("payments", typeof(MultiCluster.Payments.Contracts.IPaymentsService))]

namespace MultiCluster.Payments.Contracts;

[RpcContract]
public interface IPaymentsService : IService
{
    ValueTask<string> GetClusterAsync(CancellationToken cancellationToken);
}
