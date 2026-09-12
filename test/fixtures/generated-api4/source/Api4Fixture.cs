using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Api4Fixture;

[RpcSerializable]
public sealed class Api4Payload
{
    [RpcMember(1)]
    public int Value { get; set; }

    [RpcMember(2)]
    public string Label { get; set; } = string.Empty;
}

[RpcContract]
public interface IApi4FixtureService : IService
{
    [NonCancellable]
    ValueTask<Api4Payload> UnaryAsync(Api4Payload value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(int value);

    ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);

    IAsyncEnumerable<int> ServerStreamAsync(
        int count,
        CancellationToken cancellationToken);

    IAsyncEnumerable<int> DuplexAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);
}

[RpcService]
public sealed class Api4FixtureService : IApi4FixtureService
{
    public ValueTask<Api4Payload> UnaryAsync(Api4Payload value)
        => ValueTask.FromResult(new Api4Payload
        {
            Value = value.Value + 1,
            Label = value.Label + "-api4",
        });

    public ValueTask NotifyAsync(int value)
        => ValueTask.CompletedTask;

    public async ValueTask<int> ClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> ServerStreamAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> DuplexAsync(
        IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return value * 2;
    }
}
