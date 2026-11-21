using System.Runtime.CompilerServices;
using SharpLink.Sdk;

namespace SharpLink.CodecPublicationDrainProvider;

[RpcContract]
public interface IDeliveryUpdates : IService
{
    ValueTask<int> WaitForApprovalAsync(CancellationToken cancellationToken);

    [Oneway]
    ValueTask PostAsync(int parcelCount, CancellationToken cancellationToken);

    [Oneway]
    ValueTask UploadAsync(IAsyncEnumerable<FeedReading> readings, CancellationToken cancellationToken);

    ValueTask<int> ReconcileAsync(IAsyncEnumerable<FeedReading> readings, CancellationToken cancellationToken);

    IAsyncEnumerable<FeedReading> MirrorAsync(IAsyncEnumerable<FeedReading> readings, CancellationToken cancellationToken);
}

[RpcService]
public sealed class DeliveryUpdates : IDeliveryUpdates
{
    private static readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource Approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static int Posted;
    public static int Uploaded;
    public static int Reconciled;
    public static int TwoWayInputExits;
    public static Task WaitingForApproval => Waiting.Task;
    public static void Approve() => Approval.TrySetResult();

    public async ValueTask<int> WaitForApprovalAsync(CancellationToken cancellationToken)
    {
        Waiting.TrySetResult();
        await Approval.Task.WaitAsync(cancellationToken);
        return 42;
    }

    public ValueTask PostAsync(int parcelCount, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref Posted, parcelCount);
        return ValueTask.CompletedTask;
    }

    public async ValueTask UploadAsync(IAsyncEnumerable<FeedReading> readings, CancellationToken cancellationToken)
    {
        await foreach (var reading in readings.WithCancellation(cancellationToken))
            Interlocked.Add(ref Uploaded, reading.Value);
    }

    public async ValueTask<int> ReconcileAsync(IAsyncEnumerable<FeedReading> readings, CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            await foreach (var reading in readings.WithCancellation(cancellationToken))
            {
                Interlocked.Add(ref Reconciled, reading.Value);
                total += reading.Value;
            }
            return total;
        }
        finally { Interlocked.Increment(ref TwoWayInputExits); }
    }

    public async IAsyncEnumerable<FeedReading> MirrorAsync(IAsyncEnumerable<FeedReading> readings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in readings.WithCancellation(cancellationToken))
            {
                Interlocked.Add(ref Reconciled, reading.Value);
                yield return new(reading.Value * 2);
            }
        }
        finally { Interlocked.Increment(ref TwoWayInputExits); }
    }
}
