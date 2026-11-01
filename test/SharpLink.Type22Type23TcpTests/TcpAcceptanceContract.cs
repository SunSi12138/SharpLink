namespace SharpLink.Type22Type23TcpTests;

// Seven CompilerFiveFamily shapes, now with actual generated server execution.
[RpcContract]
public interface ITcpAcceptanceService : IService
{
    ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken);
    [Oneway] ValueTask OneWayAsync(int value, CancellationToken cancellationToken);
    ValueTask<int> ClientStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> ServerStreamingAsync(int value, CancellationToken cancellationToken);
    IAsyncEnumerable<int> DuplexStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    ValueTask<int> ClientStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> DuplexStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    // Genuine two-stream declaration; manual callers must use its generated descriptor unchanged.
    [Oneway] ValueTask NotifyPairAsync(IAsyncEnumerable<int> first, IAsyncEnumerable<int> second, CancellationToken cancellationToken);
}

[RpcService]
public sealed class TcpAcceptanceService : ITcpAcceptanceService
{
    private int _calls, _pairItems;
    public int Calls => Volatile.Read(ref _calls);
    public int PairItems => Volatile.Read(ref _pairItems);
    public bool ReturnWithoutReadingPair { get; set; }
    public TaskCompletionSource<int> Notification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PairEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<Exception> PairFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PairExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<int> PairResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _calls); return ValueTask.FromResult(value + 1); }
    public ValueTask OneWayAsync(int value, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _calls); Notification.TrySetResult(value); return ValueTask.CompletedTask; }
    public async ValueTask<int> ClientStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _calls); return await SumAsync(values, cancellationToken); }
    public async ValueTask<int> ClientStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    { Interlocked.Increment(ref _calls); return seed + await SumAsync(values, cancellationToken); }
    public async IAsyncEnumerable<int> ServerStreamingAsync(int value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
        await Task.Yield();
        yield return value + 1;
    }
    public async IAsyncEnumerable<int> DuplexStreamingAsync(IAsyncEnumerable<int> values, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return value * 2;
    }
    public async IAsyncEnumerable<int> DuplexStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return seed + value * 2;
    }
    public async ValueTask NotifyPairAsync(IAsyncEnumerable<int> first, IAsyncEnumerable<int> second, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        PairEntered.TrySetResult();
        try
        {
            if (ReturnWithoutReadingPair) { PairResult.TrySetResult(-1); return; }
            var sum = 0;
            await foreach (var value in first.WithCancellation(cancellationToken))
            { Interlocked.Increment(ref _pairItems); sum += value; }
            await foreach (var value in second.WithCancellation(cancellationToken))
            { Interlocked.Increment(ref _pairItems); sum += value; }
            PairResult.TrySetResult(sum);
        }
        catch (Exception error) { PairFailure.TrySetResult(error); throw; }
        finally { PairExited.TrySetResult(); }
    }
    private static async ValueTask<int> SumAsync(IAsyncEnumerable<int> values, CancellationToken token)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(token)) sum += value;
        return sum;
    }
}
