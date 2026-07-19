using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IDynamicPluginService : IService
{
    ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken);

    [Oneway]
    ValueTask NotifyAsync(int value, CancellationToken cancellationToken);

    ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    IAsyncEnumerable<int> ServerStreamAsync(
        int count,
        CancellationToken cancellationToken);

    IAsyncEnumerable<int> DuplexAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    ValueTask<int> BlockAsync(CancellationToken cancellationToken);

    ValueTask<int> BlockIgnoringCancellationAsync(CancellationToken cancellationToken);
}
