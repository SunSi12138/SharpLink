using SharpLink.Sdk;
namespace HotCost.Dynamic;

[RpcService]
public sealed class HotCostDynamicService : IHotCostDynamicRpc, IDisposable
{
    private static int _created, _disposed;
    public HotCostDynamicService() => Interlocked.Increment(ref _created);
    public static int ResultOffset { get; set; }
    public static int Created => Volatile.Read(ref _created);
    public static int Disposed => Volatile.Read(ref _disposed);
    public ValueTask<int> Add(int left, int right) => ValueTask.FromResult(left + right + ResultOffset);
    private static int _oneWayCount;
    public static int OneWayCount => Volatile.Read(ref _oneWayCount);
    public async ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers)
    {
        var sum = 0; await foreach (var value in numbers) sum += value; return sum;
    }
    public async ValueTask UploadOneWay(IAsyncEnumerable<int> numbers)
    {
        var sum = 0; await foreach (var value in numbers) sum += value;
        if (sum != 6) throw new InvalidOperationException("Unexpected one-way stream content.");
        Interlocked.Increment(ref _oneWayCount);
    }
    public async IAsyncEnumerable<int> DownloadNumbers(int count)
    {
        await Task.CompletedTask; for (var i = 0; i < count; i++) yield return i;
    }
    public async IAsyncEnumerable<int> Echo(IAsyncEnumerable<int> numbers)
    {
        await foreach (var value in numbers) yield return value;
    }
    public void Dispose() => Interlocked.Increment(ref _disposed);
}
