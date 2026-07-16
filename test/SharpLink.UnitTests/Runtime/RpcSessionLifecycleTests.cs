using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class RpcSessionLifecycleTests
{
    [Test]
    public async Task FirstFaultShouldBePublishedOnceAndReusedByLaterSends()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disconnectCount = 0;
        await using var session = new RpcSession(
            "first-fault",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var publishedCount = 0;
        Exception? published = null;
        session.OnDisconnected += exception =>
        {
            published = exception;
            Interlocked.Increment(ref publishedCount);
        };
        var first = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "bad frame");

        session.NotifyDisconnected(first);
        session.NotifyDisconnected(new IOException("later failure"));

        var thrown = CaptureSendException(session);
        Ensure(ReferenceEquals(first, published), "the first structured failure should be published");
        Ensure(ReferenceEquals(first, thrown), "later sends should receive the first failure instance");
        Ensure(publishedCount == 1, "disconnect should be published once");
        Ensure(disconnectCount == 1, "transport should be disconnected once");
    }

    [Test]
    public async Task ConcurrentReadAndWriteFaultsShouldConvergeToOneTerminalState()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disconnectCount = 0;
        await using var session = new RpcSession(
            "concurrent-fault",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCount = 0;
        session.OnDisconnected += exception =>
        {
            Interlocked.Increment(ref publishedCount);
            disconnected.TrySetResult(exception);
        };
        await output.Reader.CompleteAsync();
        var readFailure = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "reader failed");

        var readFault = Task.Run(() => session.NotifyDisconnected(readFailure));
        var writeFault = Task.Run(() => session.SendPacket(CreatePacket()));
        try
        {
            await writeFault;
        }
        catch (SharpLinkException)
        {
        }
        await readFault;

        var terminal = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var laterSend = CaptureSendException(session);
        Ensure(terminal is SharpLinkException, "terminal failure should be structured");
        Ensure(ReferenceEquals(terminal, laterSend), "all waiters should observe the terminal instance");
        Ensure(publishedCount == 1, "competing faults should publish one disconnect");
        Ensure(disconnectCount == 1, "competing faults should close transport once");
    }

    [Test]
    public async Task ConcurrentSendAndDisposeShouldCompletePumpAndReturnCleanly()
    {
        var input = new Pipe();
        var output = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024));
        var disconnectCount = 0;
        var session = new RpcSession(
            "send-dispose",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true);
        var failures = new ConcurrentBag<SharpLinkException>();
        var senders = new Task[4];
        for (var senderIndex = 0; senderIndex < senders.Length; senderIndex++)
        {
            senders[senderIndex] = Task.Run(() =>
            {
                for (var packetIndex = 0; packetIndex < 1000; packetIndex++)
                {
                    try
                    {
                        session.SendPacket(CreatePacket());
                    }
                    catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
                    {
                        failures.Add(exception);
                    }
                }
            });
        }

        await Task.Yield();
        var dispose1 = session.DisposeAsync().AsTask();
        var dispose2 = session.DisposeAsync().AsTask();
        await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(dispose1, dispose2).WaitAsync(TimeSpan.FromSeconds(5));

        Ensure(disconnectCount == 1, "concurrent disposal should close transport once");
        foreach (var failure in failures)
            Ensure(failure.Code == SharpLinkErrorCode.ConnectionClosed, "closed sends should be structured");
    }

    private static SharpLinkException CaptureSendException(RpcSession session)
    {
        try
        {
            session.SendPacket(CreatePacket());
            throw new Exception("send should fail after the session terminates");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static IRpcByteBufferWriter CreatePacket()
    {
        var writer = BufferWriterPool.Get();
        writer.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1);
        return writer;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
