using System.Buffers;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("dispatcher-pool")]
public class PooledAsyncStreamDispatcherLocalAbortTests
{
    [Test]
    public async Task LocalAbortShouldPreemptBufferedDeliveryAndReturnCredit()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 71,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(1));
        await dispatcher.DispatchAsync(Encode(2));
        await dispatcher.DispatchAsync(Encode(3));

        var terminal = new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "test deadline");
        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        localAbort.CompleteLocalAbort(terminal);

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "a local deadline terminal must win before any buffered response item is delivered");
        Ensure(Volatile.Read(ref creditedBytes) == 0,
            "buffered receive credit must remain owned until local-abort retirement");

        localAbort.RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref creditedBytes) == 12,
            "retiring three discarded Int32 frames must return all buffered receive credit");

        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalAbortShouldWaitForOwnedBufferedPublication()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        var deliveryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDelivery = new ManualResetEventSlim();
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) =>
            {
                deliveryEntered.TrySetResult();
                if (!releaseDelivery.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("buffered delivery publication was not released");
                Interlocked.Add(ref creditedBytes, bytes);
            },
            requestId: 73,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();
        await dispatcher.DispatchAsync(Encode(41));

        var delivery = Task.Run(async () =>
            await enumerator.MoveNextAsync().ConfigureAwait(false));
        await deliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var terminal = new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "test deadline");
        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        var abortStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = Task.Run(() =>
        {
            abortStarted.TrySetResult();
            localAbort.CompleteLocalAbort(terminal);
        });
        await abortStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var abortWaitedForPublication = !abort.IsCompleted;

        releaseDelivery.Set();
        var delivered = await delivery.WaitAsync(TimeSpan.FromSeconds(2));
        await abort.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(abortWaitedForPublication,
            "a local terminal must not complete while an owned item is still publishing Current or receive credit");
        Ensure(delivered && enumerator.Current == 41,
            "the item that acquired publication ownership first must finish before the local terminal wins");
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "the winning delivery must publish its receive credit exactly once");

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the local terminal must close every delivery after the already-owned item");

        localAbort.RetireLocalAbortBuffer();
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "retirement must not return credit twice for an item that already published");
        await enumerator.DisposeAsync();
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task LocalAbortCleanupRetentionShouldOutliveEarlyConsumerDispose()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var creditedBytes = 0;
        dispatcher.SetBytesConsumedCallback(
            (_, _, bytes) => Interlocked.Add(ref creditedBytes, bytes),
            requestId: 72,
            streamId: 0);
        var enumerator = dispatcher.GetAsyncEnumerator();
        await dispatcher.DispatchAsync(Encode(7));

        var localAbort = (IStreamLocalAbortDispatcher)dispatcher;
        localAbort.CompleteLocalAbort(new SharpLinkException(
            SharpLinkErrorCode.DeadlineExceeded,
            "test deadline"));

        // Consumer disposal may race the connection's dispatch-drain continuation. It may
        // perform the one-shot buffer drain, but it must not return the dispatcher to the
        // process-wide pool before the terminal owner releases its cleanup retention.
        await enumerator.DisposeAsync();
        Ensure(Volatile.Read(ref creditedBytes) == 4,
            "early disposal must preserve receive credit while sharing local-abort retirement");
        Ensure(PooledAsyncStreamDispatcher<int>.RetainedCountForTests == 0,
            "the terminal cleanup retention must keep a disposed dispatcher out of the pool");

        localAbort.RetireLocalAbortBuffer();
        Ensure(PooledAsyncStreamDispatcher<int>.RetainedCountForTests == 1,
            "releasing terminal cleanup ownership must make the finalized dispatcher reusable");
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
    }

    [Test]
    public async Task RemoteTerminalShouldStillDrainBufferedItemsBeforeError()
    {
        PooledAsyncStreamDispatcher<int>.ClearPoolForTests();
        var dispatcher = PooledAsyncStreamDispatcher<int>.Rent(default, Int32Codec.Instance);
        var enumerator = dispatcher.GetAsyncEnumerator();

        await dispatcher.DispatchAsync(Encode(11));
        await dispatcher.DispatchAsync(Encode(22));
        dispatcher.Complete(new SharpLinkException(
            SharpLinkErrorCode.RemoteError,
            "peer terminal"));

        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 11,
            "a genuine peer terminal must preserve delivery of the first preceding buffered item");
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 22,
            "a genuine peer terminal must preserve delivery of all preceding buffered items");

        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.RemoteError },
            "the peer terminal error must surface only after preceding buffered items drain");

        await enumerator.DisposeAsync();
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
