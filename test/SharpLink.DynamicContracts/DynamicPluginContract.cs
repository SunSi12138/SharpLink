using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

public sealed record DynamicPayload(int Value, string Label);

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

    ValueTask<int> UsePayloadAsync(DynamicPayload payload, CancellationToken cancellationToken);
}

[RpcContract]
public interface IFirstThrowingDisposalService : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}

[RpcContract]
public interface ISecondThrowingDisposalService : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}
