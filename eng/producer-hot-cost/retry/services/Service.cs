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
    public void Dispose() => Interlocked.Increment(ref _disposed);
}
