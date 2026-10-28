using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Sdk;
namespace MethodFactsExperiment;
[RpcContract]
public interface IFactsRpc : IService
{
    [NonCancellable] IAsyncEnumerable<int> Plain(int count);
    [NonCancellable] IAsyncEnumerable<int> Queued(int count);
    [Timeout(3600)] IAsyncEnumerable<int> Tracked(int count, CancellationToken cancellationToken);
}
[RpcService]
public sealed class FactsService : IFactsRpc
{
    internal static TaskCompletionSource? QueuedGate;
    public async IAsyncEnumerable<int> Queued(int count)
    {
        if (QueuedGate is { } gate) await gate.Task;
        await Task.Yield();
        for (var i=1;i<=count;i++) yield return i;
    }
    public IAsyncEnumerable<int> Plain(int count) => new Range(count, default);
    public IAsyncEnumerable<int> Tracked(int count, CancellationToken cancellationToken) => new Range(count, cancellationToken);
    private sealed class Range(int count, CancellationToken token) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _index;
        public int Current => count < 0 ? (token.CanBeCanceled ? 1 : 0) : _index;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index <= Math.Max(1, count) && count != 0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
