using System.Threading.Tasks.Sources;
namespace SharpLink.OneWayLocalEvidence;

// Reused across serial invocations. A real generated stream writer calls the actual
// ClientConnection producer path, but no item payload or flow control dominates the benchmark.
internal sealed class ControlledEmptyStream(bool suspended)
    : IAsyncEnumerable<int>, IAsyncEnumerator<int>, IValueTaskSource<bool>
{
    private ManualResetValueTaskSourceCore<bool> _source;
    private bool _pending;
    public long Enumerations { get; private set; }
    public long Disposals { get; private set; }
    public int Current => throw new InvalidOperationException("Empty stream.");
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enumerations++;
        return this;
    }
    public ValueTask<bool> MoveNextAsync()
    {
        if (!suspended) return new(false);
        _source.Reset();
        _pending = true;
        return new(this, _source.Version);
    }
    public void Release()
    {
        if (!_pending) return;
        _pending = false;
        _source.SetResult(false);
    }
    public ValueTask DisposeAsync() { Disposals++; return default; }
    public bool GetResult(short token) => _source.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);
}
