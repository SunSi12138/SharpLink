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
    public static int LastReceivedCount { get; private set; }
    public static TaskCompletionSource StreamStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static void ResetStreamProbe() => StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers, CancellationToken cancellationToken = default)
    {
        var sum = 0; var first = true; var received = 0;
        await foreach (var number in numbers.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (first) { first = false; StreamStarted.TrySetResult(); }
            sum = checked(sum + number); received++;
        }
        LastReceivedCount = received;
        return sum;
    }
    public void Dispose() => Interlocked.Increment(ref _disposed);
}
