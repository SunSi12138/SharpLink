using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.UnitTests.Runtime;

/// <summary>
/// Issue 157 lifecycle coverage: every terminal path must wake an idle send pump that is
/// parked in <c>ChannelReader.WaitToReadAsync</c>, so the wait may safely become
/// non-cancellable and rely on Channel completion/fault alone.
/// Every test has bounded completion; none may rely on an unbounded wait to "prove" liveness.
/// </summary>
public class SendPumpIdleShutdownTests
{
    [Test]
    public async Task IdlePumpExitsWhenSessionIsDisposed()
    {
        var (session, input, output, _, _) = CreateIdleSession();
        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(session.QueuedSendBytes == 0,
                "dispose of an idle pump must release all reserved queue bytes");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
        }
    }

    [Test]
    public async Task IdlePumpExitsWhenSessionFaultsFromRemoteDisconnect()
    {
        var (session, input, output, _, _) = CreateIdleSession();
        try
        {
            session.NotifyDisconnected(new IOException("remote read failed"));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(session.QueuedSendBytes == 0,
                "a faulted session must drain an idle pump without waiting for cancellation");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
        }
    }

    [Test]
    public async Task IdlePumpExitsWhenSessionIsStopping()
    {
        var (session, input, output, _, _) = CreateIdleSession();
        try
        {
            session.BeginShutdown();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(session.QueuedSendBytes == 0,
                "BeginShutdown must stop an idle pump without an intervening send");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
        }
    }

    [Test]
    public async Task PumpBlockedInFlushExitsWhenTransportOutputFaults()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 1024 * 1024);
        try
        {
            var frame = CreateFrame(session, 32, requestId: 1);
            var flush = session.SendPacketAndFlushAsync(frame).AsTask();
            await WaitUntilAsync(() => session.QueuedSendBytes > 0);

            // A real transport output fault is delivered twice: the transport completes its
            // output pipe with the fault, and then notifies the session so the session
            // cancellation tears the pump down. A faulted pipe alone never completes a pending
            // FlushAsync (the pipe surfaces writer faults only to the reader), so the pump
            // relies on the session cancellation to wake it from a paused flush.
            var fault = new IOException("output fault");
            await output.Writer.CompleteAsync(fault);
            session.NotifyDisconnected(fault);

            var completionException = await CaptureCompletionExceptionAsync(flush, TimeSpan.FromSeconds(5));
            Ensure(completionException is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } or
                   OperationCanceledException,
                "a transport output fault must fault the pending flush completion");
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            EnsureReturned(frame, "an output-faulted flush must return its frame owner");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
        }
    }

    [Test]
    public async Task PumpBlockedInFlushExitsWhenSessionIsDisposed()
    {
        var input = new Pipe();
        var output = CreateBackpressuredPipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 1024 * 1024);
        try
        {
            var frame = CreateFrame(session, 32, requestId: 1);
            var flush = session.SendPacketAndFlushAsync(frame).AsTask();
            await WaitUntilAsync(() => session.QueuedSendBytes > 0);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var fault = await CaptureCompletionExceptionAsync(flush, TimeSpan.FromSeconds(5));
            Ensure(fault is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } or
                   OperationCanceledException,
                "dispose while a flush is pending must fault the pending flush completion");
            EnsureReturned(frame, "dispose during a pending flush must return its frame owner");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
        }
    }

    [Test]
    public async Task PumpParkedInTimedBatchDeadlineExitsWhenSessionIsDisposed()
    {
        var clock = new ManualTimeProvider();
        var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "timed-batch-idle-shutdown",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(5))));
        try
        {
            var frame = CreateFrame(session, 32, requestId: 1);
            session.SendPacket(frame);
            await WaitUntilAsync(() => clock.ActiveTimerCount > 0);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            EnsureReturned(frame, "a deadline-parked pump must return its batched frame on dispose");
            Ensure(session.QueuedSendBytes == 0 && clock.ActiveTimerCount == 0,
                "dispose must drain the deadline-parked pump and disarm its timer");
        }
        finally
        {
            await CompletePipelinesAsync(input, output);
            context.Dispose();
        }
    }

    [Test]
    public async Task ConcurrentProducersAndShutdownCompleteWithinBound()
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession(input, output, maxSendQueueBytes: 64 * 1024);
        using var producersStopped = new CancellationTokenSource();
        using var producersReady = new CountdownEvent(4);
        using var startProducers = new ManualResetEventSlim(initialState: false);
        using var producersAtFirstSend = new CountdownEvent(4);
        var producers = new Task[4];
        try
        {
            for (var index = 0; index < producers.Length; index++)
            {
                var producerIndex = index;
                producers[index] = LongRunningTestWorker.Run(() =>
                {
                    ulong requestId = (ulong)(producerIndex + 1) * 10_000;
                    var firstSend = true;
                    producersReady.Signal();
                    startProducers.Wait();
                    while (!producersStopped.IsCancellationRequested)
                    {
                        try
                        {
                            var frame = CreateFrame(session, 32, requestId++);
                            if (firstSend)
                            {
                                // The phase barrier must precede the operation under test. A
                                // synchronous send may itself wait on send-pump progress that
                                // the concurrent shutdown below is intended to race.
                                producersAtFirstSend.Signal();
                                firstSend = false;
                            }

                            if (producerIndex == producers.Length - 1)
                            {
                                var flush = session.SendPacketAndFlushAsync(frame).AsTask();
                                flush.GetAwaiter().GetResult();
                            }
                            else
                            {
                                session.SendPacket(frame);
                            }
                        }
                        catch (SharpLinkException)
                        {
                            return;
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                    }
                });
            }

            Ensure(producersReady.Wait(TimeSpan.FromSeconds(5)),
                "all dedicated producers must reach the start gate before shutdown begins");
            startProducers.Set();
            Ensure(producersAtFirstSend.Wait(TimeSpan.FromSeconds(5)),
                "all dedicated producers must reach their first send attempt before shutdown begins");

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            producersStopped.Cancel();
            await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10));

            Ensure(session.QueuedSendBytes == 0,
                "concurrent producers must observe shutdown and release every reserved byte");
        }
        finally
        {
            producersStopped.Cancel();
            startProducers.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var producer in producers)
            {
                if (producer is not null)
                    await LongRunningTestWorker.JoinAsync(producer, TimeSpan.FromSeconds(10));
            }
            await CompletePipelinesAsync(input, output);
        }
    }

    private static (RpcSession Session, Pipe Input, Pipe Output, IRpcByteBufferWriter Frame, SharpLinkRuntimeContext Context)
        CreateIdleSession()
    {
        var input = new Pipe();
        var output = new Pipe();
        var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "idle-pump-shutdown",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        var frame = CreateFrame(session, 32, requestId: 1);
        session.SendPacket(frame);
        ConsumeAndSettle(output.Reader).GetAwaiter().GetResult();
        return (session, input, output, frame, context);
    }

    private static async Task ConsumeAndSettle(PipeReader reader)
    {
        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        reader.AdvanceTo(read.Buffer.End);
        await Task.Delay(50);
    }

    private static async Task CompletePipelinesAsync(Pipe input, Pipe output)
    {
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static RpcSession CreateSession(Pipe input, Pipe output, int maxSendQueueBytes)
    {
        var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxSendQueueBytes = maxSendQueueBytes)
            .Build(includeGeneratedAssemblyCatalog: false);
        return RpcSessionTestFixture.CreateSessionOverTestTransport(
            "send-pump-idle-shutdown",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
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

    private static async Task<Exception?> CaptureCompletionExceptionAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
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
}
