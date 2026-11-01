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
    private static TaskCompletionSource _inputEntered = NewGate(), _incomingDisposeObserved = NewGate();
    private static TaskCompletionSource _duplexFinalDisposeEntered = NewGate(), _duplexFinalDisposeRelease = NewGate();
    private static object? _incomingEnumerator;
    private static Task? _incomingDisposeTask;
    private static int _incomingDisposeCalls, _incomingDisposed, _incomingDisposeWasIncomplete;

    public static Task InputEntered => _inputEntered.Task;
    public static Task IncomingDisposeObserved => _incomingDisposeObserved.Task;
    public static Task DuplexFinalDisposeEntered => _duplexFinalDisposeEntered.Task;
    public static object? IncomingEnumerator => Volatile.Read(ref _incomingEnumerator);
    public static Task? IncomingDisposeTask => Volatile.Read(ref _incomingDisposeTask);
    public static int IncomingDisposeCalls => Volatile.Read(ref _incomingDisposeCalls);
    public static int IncomingDisposed => Volatile.Read(ref _incomingDisposed);
    public static bool IncomingDisposeWasIncomplete => Volatile.Read(ref _incomingDisposeWasIncomplete) != 0;
    public static void ReleaseDuplexFinalDispose() => _duplexFinalDisposeRelease.TrySetResult();

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
        _inputEntered = NewGate(); _incomingDisposeObserved = NewGate();
        _duplexFinalDisposeEntered = NewGate(); _duplexFinalDisposeRelease = NewGate();
        _incomingEnumerator = null; _incomingDisposeTask = null;
        _incomingDisposeCalls = _incomingDisposed = _incomingDisposeWasIncomplete = 0;
        PhysicalTailScopeProbe.Reset();
    }

    public IAsyncEnumerable<int> BlockPhysicalTailAsync(CancellationToken cancellationToken) => new PhysicalStream(_scope);

    public IAsyncEnumerable<int> BlockDuplexPhysicalTailAsync(
        IAsyncEnumerable<PhysicalTailPayload> incoming, CancellationToken cancellationToken)
        => new DuplexPhysicalStream(_scope, incoming.GetAsyncEnumerator(cancellationToken));

    private sealed class DuplexPhysicalStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly PhysicalTailScopeProbe _scope;
        private readonly IAsyncEnumerator<PhysicalTailPayload> _incoming;
        private CancellationToken _token;
        internal DuplexPhysicalStream(PhysicalTailScopeProbe scope, IAsyncEnumerator<PhysicalTailPayload> incoming)
        {
            _scope = scope; _incoming = incoming;
            Volatile.Write(ref _incomingEnumerator, incoming);
            _inputEntered.TrySetResult();
        }
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            _moveEntered.TrySetResult();
            await _moveRelease.Task.ConfigureAwait(false); // actual noncooperative outgoing operation
            CheckPhysicalOwners();
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            _enumeratorDisposeEntered.TrySetResult();
            CheckPhysicalOwners();
            Interlocked.Increment(ref _incomingDisposeCalls);
            var disposal = _incoming.DisposeAsync(); // original generated incoming wrapper, exactly once
            if (!disposal.IsCompleted)
            {
                var originalTask = disposal.AsTask(); // consume the original incomplete ValueTask once
                Volatile.Write(ref _incomingDisposeWasIncomplete, 1);
                Volatile.Write(ref _incomingDisposeTask, originalTask);
                _incomingDisposeObserved.TrySetResult();
                await originalTask.ConfigureAwait(false);
            }
            else
            {
                disposal.GetAwaiter().GetResult();
                _incomingDisposeObserved.TrySetResult(); // no synthetic Task for the synchronous branch
            }
            Interlocked.Increment(ref _incomingDisposed);
            _duplexFinalDisposeEntered.TrySetResult();
            await _duplexFinalDisposeRelease.Task.ConfigureAwait(false);
            CheckPhysicalOwners();
            Interlocked.Increment(ref _enumeratorDisposed);
        }
        private void CheckPhysicalOwners()
        {
            if (_scope.IsDisposed || _token.WaitHandle.SafeWaitHandle.IsClosed)
                throw new InvalidOperationException("Scope/linked CTS was disposed before its actual Duplex producer.");
            Interlocked.Increment(ref _tokenChecks);
        }
    }

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
