using Timeout = SharpLink.Sdk.TimeoutAttribute;

namespace SharpLink.IntegrationTests;

[RpcContract]
public interface ICallShapeService : IService
{
    [NonCancellable]
    ValueTask<int> UnaryPayloadAsync(int payload);
    [NonCancellable]
    ValueTask<int> UnaryNoPayloadAsync();
    ValueTask<int> UnaryCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    ValueTask<int> UnaryCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    ValueTask<int> UnaryWaitForCancellationAsync(CancellationToken cancellationToken = default);
    [Timeout(0.2)]
    ValueTask<int> UnaryAlwaysSlowWithTimeoutAsync(CancellationToken cancellationToken = default);

    [NonCancellable]
    ValueTask VoidPayloadAsync(int payload);
    [NonCancellable]
    ValueTask VoidNoPayloadAsync();
    ValueTask VoidCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    ValueTask VoidCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidCancellableNoReturnWithTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask VoidCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask VoidCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> GetVoidTotalAsync();

    [Oneway]
    [NonCancellable]
    ValueTask OneWayPayloadAsync(int payload);
    [Oneway]
    [NonCancellable]
    ValueTask OneWayNoPayloadAsync();
    [Oneway]
    ValueTask OneWayCancellableAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> GetOneWayTotalAsync();

    [NonCancellable]
    ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    [NonCancellable]
    ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    ValueTask<int> ClientStreamDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> ClientStreamDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask<int> ClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask ClientStreamNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    [NonCancellable]
    ValueTask ClientStreamNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    ValueTask ClientStreamNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask ClientStreamCancellableNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    ValueTask ClientStreamCancellableNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamCancellableNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    ValueTask ClientStreamCancellableNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);

    [Oneway]
    [NonCancellable]
    ValueTask OneWayClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream);
    [Oneway]
    [NonCancellable]
    ValueTask OneWayClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask OneWayClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamCancellableWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout]
    ValueTask OneWayClientStreamCancellableWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamCancellableWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Oneway]
    [Timeout(1)]
    ValueTask OneWayClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);

    [NonCancellable]
    ValueTask<int> GetClientStreamNoReturnTotalAsync();

    [NonCancellable]
    IAsyncEnumerable<int> ServerStreamPayloadAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<int> ServerStreamNoPayloadAsync();
    [Timeout]
    IAsyncEnumerable<int> ServerStreamDefaultTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> ServerStreamCancellableNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> ServerStreamCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default);

    [NonCancellable]
    IAsyncEnumerable<int> DuplexPayloadAsync(int add, IAsyncEnumerable<int> stream);
    [NonCancellable]
    IAsyncEnumerable<int> DuplexNoPayloadAsync(IAsyncEnumerable<int> stream);
    [Timeout]
    IAsyncEnumerable<int> DuplexDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    IAsyncEnumerable<int> DuplexCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout]
    IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
    [Timeout(1)]
    IAsyncEnumerable<int> DuplexCancellableTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class CallShapeService : ICallShapeService
{
    private int _voidTotal;
    private int _oneWayTotal;
    private int _clientStreamNoReturnTotal;

    public ValueTask<int> UnaryPayloadAsync(int payload) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryNoPayloadAsync() => ValueTask.FromResult(7);
    public ValueTask<int> UnaryCancellableAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 100);
    public ValueTask<int> UnaryDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(108);
    public ValueTask<int> UnaryTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 100);
    public ValueTask<int> UnaryTimeoutNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(110);
    public ValueTask<int> UnaryNonCancellableDefaultTimeoutPayloadAsync(int payload) => ValueTask.FromResult(payload + 200);
    public ValueTask<int> UnaryNonCancellableDefaultTimeoutNoPayloadAsync() => ValueTask.FromResult(208);
    public ValueTask<int> UnaryNonCancellableTimeoutPayloadAsync(int payload) => ValueTask.FromResult(payload + 200);
    public ValueTask<int> UnaryNonCancellableTimeoutNoPayloadAsync() => ValueTask.FromResult(210);
    public ValueTask<int> UnaryCancellableNoPayloadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(111);
    public ValueTask<int> UnaryCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public ValueTask<int> UnaryCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(payload + 10);
    public async ValueTask<int> UnaryWaitForCancellationAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(global::System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public async ValueTask<int> UnaryAlwaysSlowWithTimeoutAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        return 1;
    }

    public ValueTask VoidPayloadAsync(int payload)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidNoPayloadAsync()
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableNoReturnWithTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _voidTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask VoidCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _voidTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetVoidTotalAsync() => ValueTask.FromResult(_voidTotal);

    public ValueTask OneWayPayloadAsync(int payload)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayNoPayloadAsync()
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayDefaultTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayTimeoutPayloadAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableDefaultTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableDefaultTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableTimeoutAsync(int payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref _oneWayTotal, payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask OneWayCancellableTimeoutNoPayloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _oneWayTotal);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> GetOneWayTotalAsync() => ValueTask.FromResult(_oneWayTotal);

    public async ValueTask<int> ClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream)
        => marker + await SumAsync(stream);

    public async ValueTask<int> ClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream)
        => await SumAsync(stream).ConfigureAwait(false);

    public async ValueTask<int> ClientStreamDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => await SumAsync(stream, cancellationToken);

    public async ValueTask ClientStreamNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask<int> ClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask<int> ClientStreamCancellableTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
        => marker + await SumAsync(stream, cancellationToken);

    public async ValueTask ClientStreamCancellableNoReturnPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask ClientStreamCancellableNoReturnWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamPayloadAsync(int marker, IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        var sum = await SumAsync(stream);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellablePayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithDefaultTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithTimeoutPayloadAsync(int marker, IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, marker + sum);
    }

    public async ValueTask OneWayClientStreamCancellableWithTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = await SumAsync(stream, cancellationToken);
        Interlocked.Add(ref _clientStreamNoReturnTotal, sum);
    }

    public ValueTask<int> GetClientStreamNoReturnTotalAsync() => ValueTask.FromResult(_clientStreamNoReturnTotal);

    public async IAsyncEnumerable<int> ServerStreamPayloadAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamNoPayloadAsync()
    {
        yield return 9;
        await Task.Yield();
        yield return 8;
    }

    public async IAsyncEnumerable<int> ServerStreamDefaultTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            yield return 40 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamDefaultTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 42;
        await Task.Yield();
        yield return 43;
    }

    public async IAsyncEnumerable<int> ServerStreamTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            yield return 44 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 46;
        await Task.Yield();
        yield return 47;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellablePayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 10 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 48;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 49;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 20 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableDefaultTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 50;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 51;
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableTimeoutPayloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return 30 + i;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> ServerStreamCancellableTimeoutNoPayloadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return 52;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return 53;
    }

    public async IAsyncEnumerable<int> DuplexPayloadAsync(int add, IAsyncEnumerable<int> stream)
    {
        await foreach (var item in stream)
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexNoPayloadAsync(IAsyncEnumerable<int> stream)
    {
        await foreach (var item in stream)
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellablePayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableDefaultTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<int> DuplexCancellableTimeoutPayloadAsync(int add, IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item + add;
    }

    public async IAsyncEnumerable<int> DuplexCancellableTimeoutNoPayloadAsync(IAsyncEnumerable<int> stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    private static async Task<int> SumAsync(IAsyncEnumerable<int> stream, CancellationToken cancellationToken = default)
    {
        var sum = 0;
        await foreach (var v in stream.WithCancellation(cancellationToken))
            sum += v;
        return sum;
    }
}
