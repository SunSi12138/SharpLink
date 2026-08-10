using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class RpcSessionLifecycleTests
{
    [Test]
    public void CreationOptionsShouldRejectMissingContextAndUnknownRole()
    {
        var missingContext = CaptureException(() =>
            _ = new RpcSessionCreationOptions(RpcSessionRole.Client, null!));
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var unknownRole = CaptureException(() =>
            _ = new RpcSessionCreationOptions((RpcSessionRole)byte.MaxValue, context));

        Ensure(missingContext is ArgumentNullException { ParamName: "runtimeContext" },
            "Session creation must reject a missing RuntimeContext before transport ownership transfers");
        Ensure(unknownRole is ArgumentOutOfRangeException { ParamName: "role" },
            "Session creation must reject an unknown role before transport ownership transfers");
    }

    [Test]
    public async Task ConstructorShouldPublishCompleteRoleContextMapperAndStableStreamManager()
    {
        using var clientContext = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.Protocol.MaxFramePayloadBytes = 2048)
            .ConfigureStateStores(static options => options.StripeCount = 8)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var serverContext = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.Protocol.MaxFramePayloadBytes = 4096)
            .ConfigureStateStores(static options => options.StripeCount = 16)
            .Build(includeGeneratedAssemblyCatalog: false);
        var clientInput = new Pipe();
        var clientOutput = new Pipe();
        var serverInput = new Pipe();
        var serverOutput = new Pipe();
        var mapped = new SharpLinkException(SharpLinkErrorCode.Internal, "mapped during construction");
        var mapperCalls = 0;
        var client = new RpcSession(
            "complete-client",
            clientInput.Reader,
            clientOutput.Writer,
            static () => { },
            static () => true,
            new RpcSessionCreationOptions(RpcSessionRole.Client, clientContext));
        var server = new RpcSession(
            "complete-server",
            serverInput.Reader,
            serverOutput.Writer,
            static () => { },
            static () => true,
            new RpcSessionCreationOptions(
                RpcSessionRole.Server,
                serverContext,
                serviceExceptionMapper: (_, _, _, _, _) =>
                {
                    Interlocked.Increment(ref mapperCalls);
                    return mapped;
                }));
        var clientStreams = client.StreamManager;
        var serverStreams = server.StreamManager;

        Ensure(client.Role == RpcSessionRole.Client && server.Role == RpcSessionRole.Server,
            "constructor role must distinguish Client and Server telemetry/protocol ownership");
        Ensure(ReferenceEquals(client.RuntimeContext, clientContext) &&
               ReferenceEquals(server.RuntimeContext, serverContext),
            "each Session must publish its caller-supplied RuntimeContext immediately");
        Ensure(client.NegotiatedMaxFramePayloadBytes == 2048 &&
               server.NegotiatedMaxFramePayloadBytes == 4096,
            "each Session must snapshot protocol limits from only its own Context");
        Ensure(!ReferenceEquals(clientStreams, serverStreams),
            "parallel Sessions must not share StreamManager state");
        Ensure(ReferenceEquals(mapped, server.MapServiceException(1, 2, 3, new Exception("service"))) &&
               mapperCalls == 1,
            "the Server mapper must be usable without a post-construction patch");

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
        Ensure(ReferenceEquals(clientStreams, client.StreamManager) &&
               ReferenceEquals(serverStreams, server.StreamManager),
            "StreamManager references must remain constant through terminal transitions");
        await clientInput.Writer.CompleteAsync();
        await clientOutput.Reader.CompleteAsync();
        await serverInput.Writer.CompleteAsync();
        await serverOutput.Reader.CompleteAsync();
        clientContext.Dispose();
        serverContext.Dispose();
        Ensure(CaptureException(() => clientContext.Buffers.Rent()) is ObjectDisposedException &&
               CaptureException(() => serverContext.Buffers.Rent()) is ObjectDisposedException,
            "both isolated RuntimeContexts must reject resource acquisition after deterministic disposal");
    }

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
            static () => true,
            RpcSessionTestFixture.ClientOptions());
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
            static () => true,
            RpcSessionTestFixture.ClientOptions());
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
        var writeFault = Task.Run(() => session.SendPacket(CreatePacket(session)));
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
            static () => true,
            RpcSessionTestFixture.ClientOptions());
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
                        session.SendPacket(CreatePacket(session));
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

    [Test]
    public async Task NotifyConnectedAfterDisposeShouldNotReopenConnectionMetric()
    {
        const string side = "client";
        var balance = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.connections.active")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && Equals(tag.Value, side))
                {
                    Interlocked.Add(ref balance, measurement);
                    break;
                }
            }
        });
        listener.Start();

        var input = new Pipe();
        var output = new Pipe();
        var session = new RpcSession(
            "late-notify",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true,
            RpcSessionTestFixture.ClientOptions());

        await session.DisposeAsync();
        session.NotifyConnected();

        Ensure(Volatile.Read(ref balance) == 0, "a terminal session must not reopen its connection metric");
    }

    [Test]
    public async Task ConnectionThresholdShouldSendCreditForEveryContributingStream()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "flow-credit-flush",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true,
            RpcSessionTestFixture.ClientOptions());
        session.NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl;
        session.EnableStreamFlowControl(4, 4);
        session.StreamManager.Register(1, 1, new ImmediateConsumingDispatcher());
        session.StreamManager.Register(2, 1, new ImmediateConsumingDispatcher());

        await session.StreamManager.DispatchChunkAsync(1, 1, new ReadOnlySequence<byte>(new byte[1]));
        await session.StreamManager.DispatchChunkAsync(2, 1, new ReadOnlySequence<byte>(new byte[1]));
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var frames = read.Buffer;
        var updates = new List<(ulong RequestId, ProtocolV2WindowUpdate Update)>();
        while (ProtocolV2FrameParser.TryReadFrame(
                   ref frames,
                   session.RuntimeContext.Protocol,
                   out var header,
                   out var payload))
        {
            Ensure(header.Type == ProtocolV2FrameType.WindowUpdate, "flow-control flush must only emit window updates");
            updates.Add((header.RequestId, ProtocolV2PayloadCodec.ReadWindowUpdate(payload)));
        }
        output.Reader.AdvanceTo(read.Buffer.End);

        Ensure(updates.Count == 2, "both contributing streams must receive one window update");
        Ensure(updates.Contains((1, new ProtocolV2WindowUpdate(1, 1))), "the first stream credit must be returned");
        Ensure(updates.Contains((2, new ProtocolV2WindowUpdate(1, 1))), "the triggering stream credit must be returned");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task ThrowingStreamCompletionShouldNotStrandSessionCleanup()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disconnectCount = 0;
        var session = new RpcSession(
            "throwing-stream-completion",
            input.Reader,
            output.Writer,
            () => Interlocked.Increment(ref disconnectCount),
            static () => true,
            RpcSessionTestFixture.ClientOptions());
        var sibling = new TrackingCompletionDispatcher();
        session.StreamManager.Register(1, new ThrowingCompletionDispatcher());
        session.StreamManager.Register(1, 1, sibling);

        var failure = await CaptureExceptionAsync(session.DisposeAsync().AsTask());
        if (failure is not null)
        {
            try { await session.DisposeAsync(); } catch { }
        }
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();

        Ensure(failure is null,
            "dispatcher cleanup exceptions must not interrupt Session disposal");
        Ensure(sibling.CompletionCount == 1,
            "a throwing dispatcher must not strand sibling stream completion");
        Ensure(disconnectCount == 1,
            "a throwing dispatcher must not skip transport disposal");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static SharpLinkException CaptureSendException(RpcSession session)
    {
        try
        {
            session.SendPacket(CreatePacket(session));
            throw new Exception("send should fail after the session terminates");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static IRpcByteBufferWriter CreatePacket(RpcSession session)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        writer.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, 1);
        return writer;
    }

    private sealed class ImmediateConsumingDispatcher : IStreamConsumptionAwareDispatcher
    {
        private Action<long, ushort, int>? _bytesConsumed;
        private long _requestId;
        private ushort _streamId;

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
            => DispatchAsync(payload, checked((int)payload.Length));

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
        {
            _ = payload;
            _bytesConsumed?.Invoke(_requestId, _streamId, encodedByteCount);
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
        {
            _ = isError;
            _ = errorMessage;
        }

        public void Complete(Exception? exception) => _ = exception;

        public void SetBytesConsumedCallback(
            Action<long, ushort, int>? callback,
            long requestId,
            ushort streamId)
        {
            _bytesConsumed = callback;
            _requestId = requestId;
            _streamId = streamId;
        }
    }

    private sealed class ThrowingCompletionDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage)
            => throw new InvalidOperationException("dispatcher completion failed");
        public void Complete(Exception? exception)
            => throw new InvalidOperationException("dispatcher completion failed");
    }

    private sealed class TrackingCompletionDispatcher : IStreamDispatcher
    {
        private int _completionCount;
        internal int CompletionCount => Volatile.Read(ref _completionCount);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage)
            => Interlocked.Increment(ref _completionCount);
        public void Complete(Exception? exception)
            => Interlocked.Increment(ref _completionCount);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
