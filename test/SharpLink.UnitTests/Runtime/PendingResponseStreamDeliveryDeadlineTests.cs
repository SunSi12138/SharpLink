using System.Buffers;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public class PendingResponseStreamDeliveryDeadlineTests
{
    [Test]
    public async Task BufferedItemShouldLoseToExpiredPendingDeadlineBeforeTimerRuns()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var timeProvider = new ManualTimeProvider();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var owner = new StreamingPendingOwner();
        using var pending = new PendingRequestTable(
            8,
            new Int32OnlyCodecProvider(),
            owner,
            timeProvider);
        var requestId = pending.RegisterStream(
            PendingCallKind.ServerStreaming,
            dispatcher,
            RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider),
            CancellationToken.None);
        var deliveryGate = new PendingDeliveryGate(pending);
        dispatcher.SetConsumerAbandonedCallback(
            deliveryGate.OnConsumerAbandonedAsync,
            requestId);

        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(91));
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a pre-deadline buffered item must not publish after the owning pending deadline expires");
        Ensure(pending.Count == 0,
            "delivery-time deadline arbitration must retire the pending stream even while its timer callback is delayed");
        Ensure(owner.DeadlineCompletionCount == 1,
            "the pending call owner must observe exactly one deadline terminal");
        Ensure(Volatile.Read(ref creditedBytes) == 0,
            "the rejected buffered item must not publish receive credit as a delivered item");

        ((IStreamLocalAbortDispatcher)dispatcher).RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref creditedBytes) == sizeof(int),
            "retiring the rejected buffered item must return its receive credit exactly once");

        await enumerator.DisposeAsync();
        timeProvider.Advance(TimeSpan.Zero);
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    private static ReadOnlySequence<byte> Encode(int value)
    {
        var writer = new ArrayBufferWriter<byte>();
        Int32Codec.Instance.Serialize(in value, writer);
        return new ReadOnlySequence<byte>(writer.WrittenMemory);
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Exception("expected failure");
    }

    private sealed class PendingDeliveryGate(PendingRequestTable pending) : IStreamConsumerDeliveryGate
    {
        public bool TryAcceptStreamDelivery(long requestId)
            => pending.TryAcceptStreamData(requestId);

        internal ValueTask OnConsumerAbandonedAsync(
            long requestId,
            IStreamDispatchState? dispatchState)
            => ValueTask.CompletedTask;
    }

    private sealed class StreamingPendingOwner : IPendingCallOwner
    {
        private int _deadlineCompletionCount;

        internal int DeadlineCompletionCount => Volatile.Read(ref _deadlineCompletionCount);

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            if (completion.Reason != PendingCallCompletionReason.DeadlineExceeded)
                return;

            Interlocked.Increment(ref _deadlineCompletionCount);
            if (completion.Dispatcher is IStreamLocalAbortDispatcher localAbort)
                localAbort.CompleteLocalAbort(completion.Exception);
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }

    private sealed class Int32OnlyCodecProvider : IRpcCodecProvider
    {
        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)Int32Codec.Instance;
            throw new NotSupportedException($"No test codec is registered for {typeof(T)}.");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
