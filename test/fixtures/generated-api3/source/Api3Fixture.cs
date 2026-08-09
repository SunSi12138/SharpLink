using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Api3Fixture;

[RpcSerializable]
public sealed class Api3Payload
{
    [RpcMember(1)]
    public int Value { get; set; }

    [RpcMember(2)]
    public string Label { get; set; } = string.Empty;
}

[RpcContract]
public interface IApi3FixtureService : IService
{
    [NonCancellable]
    ValueTask<Api3Payload> UnaryAsync(Api3Payload value);

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
public sealed class Api3FixtureService : IApi3FixtureService
{
    private static readonly TaskCompletionSource Notification =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _notifications;

    public static int Notifications => Volatile.Read(ref _notifications);

    public static Task NotificationObserved => Notification.Task;

    public ValueTask<Api3Payload> UnaryAsync(Api3Payload value)
        => ValueTask.FromResult(new Api3Payload
        {
            Value = value.Value + 1,
            Label = value.Label + "-api3"
        });

    public ValueTask NotifyAsync(int value)
    {
        Interlocked.Add(ref _notifications, value);
        Notification.TrySetResult();
        return ValueTask.CompletedTask;
    }

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
