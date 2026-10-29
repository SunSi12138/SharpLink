using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace PinMechanism;

[RpcContract]
public interface IPinStream : IService
{
    IAsyncEnumerable<int> DownloadAsync(CancellationToken cancellationToken);
}

[RpcService]
public sealed class PinStreamService : IPinStream
{
    public int Items;
    public bool ForceAsync;
    public long Gets, Moves, Disposes;
    public IAsyncEnumerable<int> DownloadAsync(CancellationToken cancellationToken)
        => new Producer(this, cancellationToken);

    private sealed class Producer(PinStreamService owner, CancellationToken token)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _current = -1;
        public int Current => _current;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            owner.Gets++;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            token.ThrowIfCancellationRequested(); owner.Moves++;
            return owner.ForceAsync ? MoveAsync() : ValueTask.FromResult(++_current < owner.Items);
        }
        private async ValueTask<bool> MoveAsync()
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return ++_current < owner.Items;
        }
        public ValueTask DisposeAsync()
        {
            owner.Disposes++;
            return owner.ForceAsync ? DisposeSlowAsync() : ValueTask.CompletedTask;
        }
        private static async ValueTask DisposeSlowAsync() => await Task.Yield();
    }
}
