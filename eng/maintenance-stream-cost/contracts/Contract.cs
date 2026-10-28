using SharpLink.Abstractions;
using SharpLink.Sdk;
namespace HotCost.Dynamic;
[RpcContract]
public interface IHotCostDynamicRpc : IService
{
    [Timeout(3600)]
    ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> Add(int left, int right);
}
