using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Issue 216 regression coverage: every faulted task produced while the send pump tears down must
/// be observed by the runtime so it can never reach the Task finalizer and trip the chaos
/// harness's zero-tolerance unobserved-task gate.
/// </summary>
/// <remarks>
/// <para>
/// The faulted teardown path throws <c>SharpLinkException("Transport output completed.")</c> from
/// <c>SendPump.FlushAndReleaseAsync</c>. That single exception instance is then handed to every
/// pending pump-owned task (retained channel reads and enqueuer flush waiters). Any such task that
/// is abandoned without observation fires <c>TaskScheduler.UnobservedTaskException</c> once the GC
/// finalizes it, which is exactly the intermittent chaos failure observed on the PR Quick CI gate.
/// </para>
/// <para>
/// The assertions below subscribe to the process-wide unobserved-task event and force a finalizer
/// drain; the subscription window is kept short and the counter filters on the flush-fault marker
/// (message plus <c>FlushAndReleaseAsync</c> stack frame) so parallel tests in the same process
/// cannot cross-contaminate the count.
/// </para>
/// </remarks>
public class SendPumpUnobservedTaskTests
{
    [Test]
    public async Task FaultedTeardownShouldObserveRetainedChannelReads()
    {
        var clock = new ManualTimeProvider();
        var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "faulted-teardown-retained-reads",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(5))));
        var frame = CreateFrame(session, 32, requestId: 1);
        try
        {
            session.SendPacket(frame);
            await WaitUntilAsync(() => clock.ActiveTimerCount > 0);
            // The deadline wait registered both retained channel reads before arming the timer.

            // Transport teardown: the peer stops reading. When the deadline expires the pending
            // flush observes IsCompleted, throws "Transport output completed.", and the fault
            // closes both queues while their retained reads are still registered.
            await output.Reader.CompleteAsync();
            clock.Advance(TimeSpan.FromSeconds(6));

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            EnsureReturned(frame, "a faulted teardown must return the batched frame owner");
            Ensure(session.QueuedSendBytes == 0, "a faulted teardown must release all reserved bytes");

            session = null!;
            var unobserved = await CountUnobservedFlushFaultsAsync();
            Ensure(unobserved == 0,
                $"the faulted retained channel reads must be observed at teardown, " +
                $"but {unobserved} unobserved flush-fault task(s) reached the finalizer");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
            context.Dispose();
        }
    }

    [Test]
    public async Task CancelledFlushWaiterShouldObserveLatePumpFault()
    {
        var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "cancelled-flush-waiter",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        var frame = CreateFrame(session, 32, requestId: 1);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            try
            {
                await session.SendPacketAsync(
                    frame,
                    waitForCapacity: true,
                    forceFlush: true,
                    cancellation.Token);
                throw new Exception("expected flush-wait cancellation");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            // The pump still holds the frame in a flush paused by the backpressured pipe. The
            // reader-side teardown now faults the pump and its flush waiter. The enqueuer was
            // already cancelled, so the send path itself must observe the late fault.
            await output.Reader.CompleteAsync();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            EnsureReturned(frame, "a faulted teardown must return the paused-flush frame owner");

            session = null!;
            var unobserved = await CountUnobservedFlushFaultsAsync();
            Ensure(unobserved == 0,
                $"a flush waiter abandoned by caller cancellation must still observe its late fault, " +
                $"but {unobserved} unobserved flush-fault task(s) reached the finalizer");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
            context.Dispose();
        }
    }

    private static async Task<int> CountUnobservedFlushFaultsAsync()
    {
        var count = 0;
        var handler = new EventHandler<UnobservedTaskExceptionEventArgs>((_, eventArgs) =>
        {
            if (IsFlushFault(eventArgs.Exception))
                Interlocked.Increment(ref count);
            eventArgs.SetObserved();
        });
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            for (var round = 0; round < 5; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }
            return Volatile.Read(ref count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    private static bool IsFlushFault(AggregateException aggregate)
    {
        foreach (var inner in aggregate.Flatten().InnerExceptions)
        {
            if (inner is SharpLinkException { Message: "Transport output completed." } &&
                inner.StackTrace?.Contains("FlushAndReleaseAsync", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task CompletePipelinesAsync(Pipe input, Pipe output)
    {
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static Pipe CreateBackpressuredPipe()
        => new(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 0));

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int payloadBytes, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("condition was not reached");
            await Task.Delay(5);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void EnsureReturned(IRpcByteBufferWriter writer, string message)
    {
        try
        {
            _ = writer.WrittenCount;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new Exception(message);
    }
}
