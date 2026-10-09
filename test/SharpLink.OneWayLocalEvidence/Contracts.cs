using SharpLink.Sdk;
namespace SharpLink.OneWayLocalEvidence;

[RpcContract]
public interface ILocalOneWay : IService
{
    [Oneway, NonCancellable]
    ValueTask Plain(int value);

    [Oneway, NonCancellable, Timeout(60)]
    ValueTask PlainDeadline(int value);

    [Oneway, NonCancellable]
    ValueTask One(int value, IAsyncEnumerable<int> first);

    [Oneway, NonCancellable]
    ValueTask Two(int value, IAsyncEnumerable<int> first, IAsyncEnumerable<int> second);
}
