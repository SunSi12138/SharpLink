using SharpLink.Abstractions;
using SharpLink.Sdk;
namespace HotCost.Dynamic;

[RpcContract]
public interface IHotCostDynamicRpc : IService
{
    [Timeout(3600), NonCancellable]
    ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers);
    [Oneway, NonCancellable]
    ValueTask UploadOneWay(IAsyncEnumerable<int> numbers);
    [NonCancellable]
    IAsyncEnumerable<int> DownloadNumbers(int count);
    [NonCancellable]
    IAsyncEnumerable<int> Echo(IAsyncEnumerable<int> numbers);
    [NonCancellable]
    ValueTask<int> Add(int left, int right);
}
