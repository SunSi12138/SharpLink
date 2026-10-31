using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class PhysicalTailOwnershipService : IPhysicalTailOwnershipService, IAsyncDisposable
{
    private static TaskCompletionSource _moveEntered = NewGate(), _moveRelease = NewGate();
    private static TaskCompletionSource _enumeratorDisposeEntered = NewGate(), _enumeratorDisposeRelease = NewGate();
    private static TaskCompletionSource _serviceDisposeEntered = NewGate(), _serviceDisposeRelease = NewGate();
    private static int _enumeratorDisposed, _serviceDisposed, _tokenChecks;
    private readonly PhysicalTailScopeProbe _scope;

    public PhysicalTailOwnershipService(IServiceProvider services)
        => _scope = (PhysicalTailScopeProbe)(services.GetService(typeof(PhysicalTailScopeProbe))
            ?? throw new InvalidOperationException("Physical-tail scope probe is not registered."));

    public static Task MoveEntered => _moveEntered.Task;
    public static Task EnumeratorDisposeEntered => _enumeratorDisposeEntered.Task;
    public static Task ServiceDisposeEntered => _serviceDisposeEntered.Task;
    public static int EnumeratorDisposed => Volatile.Read(ref _enumeratorDisposed);
    public static int ServiceDisposed => Volatile.Read(ref _serviceDisposed);
    public static int TokenChecks => Volatile.Read(ref _tokenChecks);
    public static void ReleaseMove() => _moveRelease.TrySetResult();
    public static void ReleaseEnumeratorDispose() => _enumeratorDisposeRelease.TrySetResult();
    public static void ReleaseServiceDispose() => _serviceDisposeRelease.TrySetResult();
    public static void Reset()
    {
        _moveEntered = NewGate(); _moveRelease = NewGate();
        _enumeratorDisposeEntered = NewGate(); _enumeratorDisposeRelease = NewGate();
        _serviceDisposeEntered = NewGate(); _serviceDisposeRelease = NewGate();
        _enumeratorDisposed = _serviceDisposed = _tokenChecks = 0;
        PhysicalTailScopeProbe.Reset();
    }

    public IAsyncEnumerable<int> BlockPhysicalTailAsync(CancellationToken cancellationToken) => new PhysicalStream(_scope);

    public async ValueTask DisposeAsync()
    {
        _serviceDisposeEntered.TrySetResult();
        await _serviceDisposeRelease.Task.ConfigureAwait(false);
        Interlocked.Increment(ref _serviceDisposed);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PhysicalStream(PhysicalTailScopeProbe scope) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private CancellationToken _token;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            _moveEntered.TrySetResult();
            await _moveRelease.Task.ConfigureAwait(false); // deliberately ignores logical cancellation
            CheckPhysicalOwners();
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            _enumeratorDisposeEntered.TrySetResult();
            await _enumeratorDisposeRelease.Task.ConfigureAwait(false);
            CheckPhysicalOwners();
            Interlocked.Increment(ref _enumeratorDisposed);
        }
        private void CheckPhysicalOwners()
        {
            if (scope.IsDisposed || _token.WaitHandle.SafeWaitHandle.IsClosed)
                throw new InvalidOperationException("Scope/linked CTS was disposed before its physical producer.");
            Interlocked.Increment(ref _tokenChecks);
        }
    }
}

public sealed class PhysicalTailScopeProbe : IAsyncDisposable
{
    private static TaskCompletionSource _disposeEntered = NewGate(), _disposeRelease = NewGate();
    private static int _disposed;
    public bool IsDisposed { get; private set; }
    public static Task DisposeEntered => _disposeEntered.Task;
    public static int Disposed => Volatile.Read(ref _disposed);
    public static void ReleaseDispose() => _disposeRelease.TrySetResult();
    public static void Reset() { _disposeEntered = NewGate(); _disposeRelease = NewGate(); _disposed = 0; }
    public async ValueTask DisposeAsync()
    {
        _disposeEntered.TrySetResult();
        await _disposeRelease.Task.ConfigureAwait(false);
        IsDisposed = true;
        Interlocked.Increment(ref _disposed);
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
