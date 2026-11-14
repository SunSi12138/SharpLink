using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.BindingTests;

[RpcContract]
public interface IFactsRpc : IService
{
    [NonCancellable] ValueTask<int> PlainAsync();
    [NonCancellable] ValueTask NoResultAsync();
    [Oneway, NonCancellable] ValueTask FireAsync();
    ValueTask<string?> NullableAsync(CancellationToken cancellationToken);
    [Timeout, Idempotent] ValueTask<int> DeclaredAsync(CancellationToken cancellationToken);
    [Timeout(0.0000001), Idempotent] ValueTask<int> TickAsync(CancellationToken cancellationToken);
    ValueTask HoldAsync(CancellationToken cancellationToken);
    ValueTask FailAsync(CancellationToken cancellationToken);
    ValueTask<int> FailResultAsync(CancellationToken cancellationToken);
    ValueTask<int> ConsumeAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> ProduceAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    [Oneway] ValueTask FireStreamAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}

public sealed class FactsService : IFactsRpc
{
    internal readonly Exception Failure = new InvalidOperationException("original facts failure");
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls;
    internal int Holding;
    public ValueTask<int> PlainAsync() { Interlocked.Increment(ref Calls); return ValueTask.FromResult(42); }
    public ValueTask NoResultAsync() { Interlocked.Increment(ref Calls); return ValueTask.CompletedTask; }
    public ValueTask FireAsync() { Interlocked.Increment(ref Calls); Fired.TrySetResult(); return ValueTask.CompletedTask; }
    public ValueTask<string?> NullableAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask<int> DeclaredAsync(CancellationToken cancellationToken) => ValueTask.FromResult(5);
    public ValueTask<int> TickAsync(CancellationToken cancellationToken) => ValueTask.FromResult(6);
    public async ValueTask HoldAsync(CancellationToken cancellationToken)
    { Volatile.Write(ref Holding, 1); Entered.TrySetResult(); try { await Release.Task.WaitAsync(cancellationToken); } finally { Volatile.Write(ref Holding, 0); } }
    public async ValueTask FailAsync(CancellationToken cancellationToken) { await Task.Yield(); throw Failure; }
    public async ValueTask<int> FailResultAsync(CancellationToken cancellationToken) { await Task.Yield(); throw Failure; }
    public async ValueTask<int> ConsumeAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    { var sum = 0; await foreach (var value in values.WithCancellation(cancellationToken)) sum += value; return sum; }
    public async IAsyncEnumerable<int> ProduceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.Yield(); yield return 3; yield return 4; }
    public async IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await foreach (var value in values.WithCancellation(cancellationToken)) yield return value * 2; }
    public async ValueTask FireStreamAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    { await foreach (var _ in values.WithCancellation(cancellationToken)) Interlocked.Increment(ref Calls); }
}
