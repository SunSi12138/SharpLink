using System.Runtime.CompilerServices;
using SharpLink.Sdk;
using SharpPack;

namespace SharpLink.DynamicPlugin;

[SharpPackable(GenerateType.CircularReference)]
public sealed partial class DynamicPayload
{
    [SharpPackOrder(0)] public int Value { get; set; }
    [SharpPackOrder(1)] public string Label { get; set; } = string.Empty;
    [SharpPackOrder(2)] public DynamicPayload? Parent { get; set; }
    [SharpPackOrder(3), SharpPackAllowSerialize] public List<int> Values { get; set; } = [];
}

[RpcContract]
public interface IDynamicPluginService : IService
{
    ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken);

    [Oneway]
    ValueTask NotifyAsync(int value, CancellationToken cancellationToken);

    ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    ValueTask<int> RejectClientStreamAsync(
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

    [NonCancellable]
    ValueTask<int> BlockSynchronously();

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

[RpcContract]
public interface IFlakyConnectionService : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}

[RpcContract]
public interface IRetiredConnectionService : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}
