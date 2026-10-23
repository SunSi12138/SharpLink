using SharpLink.Abstractions;
using SharpLink.Sdk;
namespace HotCost.Dynamic;

[RpcContract]
public interface IHotCostDynamicRpc : IService
{
    [NonCancellable, Idempotent]
    ValueTask<int> Add(int left, int right);
}
