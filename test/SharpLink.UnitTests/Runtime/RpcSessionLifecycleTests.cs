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
    public async Task ConstructorShouldPublishCompleteRoleContextAndStableStreamManager()
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
        var clientTransport = RpcSessionTestFixture.Transport(
            "complete-client",
            clientInput.Reader,
            clientOutput.Writer);
        var client = new RpcSession(
            clientTransport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, clientContext));
        var serverTransport = RpcSessionTestFixture.Transport(
            "complete-server",
            serverInput.Reader,
            serverOutput.Writer);
        var server = new RpcSession(
            serverTransport,
            new RpcSessionCreationOptions(RpcSessionRole.Server, serverContext));
        var clientStreams = client.StreamManager;
        var serverStreams = server.StreamManager;

        Ensure(client.Role == RpcSessionRole.Client && server.Role == RpcSessionRole.Server,
            "constructor role must distinguish Client and Server telemetry/protocol ownership");
        Ensure(ReferenceEquals(client.RuntimeContext, clientContext) &&
               ReferenceEquals(server.RuntimeContext, serverContext),
            "each Session must publish its caller-supplied RuntimeContext immediately");
        Ensure(client.ProtocolPhase == RpcSessionProtocolPhase.Handshaking &&
               server.ProtocolPhase == RpcSessionProtocolPhase.Handshaking &&
               client.NegotiatedOptions is null && server.NegotiatedOptions is null,
            "construction must not expose local limits as a completed negotiation");
        Ensure(client.NegotiatedMaxFramePayloadBytes == 2048 &&
               server.NegotiatedMaxFramePayloadBytes == 4096,
            "handshake frame allocation must remain bounded by each Session's local Context");
        Ensure(!ReferenceEquals(clientStreams, serverStreams),
            "parallel Sessions must not share StreamManager state");
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
    public async Task TransportDisposeShouldBeSingleFlightAcrossFaultShutdownAndDisposeRaces()
    {
        var input = new Pipe();
        var output = new Pipe();
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportFailure = new IOException("transport dispose failed");
        var transport = RpcSessionTestFixture.Transport(
            "terminal-dispose-race",
            input.Reader,
            output.Writer,
            DisposeTransportAsync);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        using var start = new ManualResetEventSlim();

        var fault = Task.Run(() =>
        {
            start.Wait();
            session.NotifyDisconnected(new IOException("read failed"));
        });
        var shutdown = Task.Run(() =>
        {
            start.Wait();
            session.BeginShutdown();
        });
        var firstDispose = Task.Run(async () =>
        {
            start.Wait();
            return await CaptureDisposeExceptionAsync(session);
        });
        var secondDispose = Task.Run(async () =>
        {
            start.Wait();
            return await CaptureDisposeExceptionAsync(session);
        });

        start.Set();
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseDispose.SetResult();
        await Task.WhenAll(fault, shutdown).WaitAsync(TimeSpan.FromSeconds(2));
        var failures = await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(transport.DisposeCount == 1,
            "Fault, BeginShutdown, and concurrent DisposeAsync calls must start transport disposal once");
        foreach (var failure in failures)
        {
            Ensure(ReferenceEquals(failure, transportFailure),
                "every DisposeAsync waiter must observe the same single-flight transport failure instance");
        }
        Ensure(!session.IsConnected,
            "the terminal winner must keep the Session disconnected after disposal fails");

        async ValueTask DisposeTransportAsync()
        {
            disposeStarted.TrySetResult();
            await releaseDispose.Task.ConfigureAwait(false);
            throw transportFailure;
        }
    }

    [Test]
    public async Task FaultPausedAfterPublishingTerminalShouldSurviveConcurrentRepeatedDispose()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "fault-cts-dispose-barrier",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        var terminalPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFault = new ManualResetEventSlim();
        using var listener = new MeterListener();
        var barrierArmed = 0;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.connections.active")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement != -1 || Volatile.Read(ref barrierArmed) == 0)
                return;

            foreach (var tag in tags)
            {
                if (tag.Key != "rpc.side" || !Equals(tag.Value, "client"))
                    continue;

                // Fault records this metric after publishing its terminal and before cancelling the CTS.
                terminalPublished.TrySetResult();
                releaseFault.Wait();
                return;
            }
        });
        listener.Start();
        session.NotifyConnected();
        Volatile.Write(ref barrierArmed, 1);
        var originalFault = new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "deterministic fault");
        Exception? publishedFault = null;
        var disconnectCount = 0;
        session.OnDisconnected += exception =>
        {
            publishedFault = exception;
            Interlocked.Increment(ref disconnectCount);
        };

        var faultTask = Task.Run(() =>
            CaptureException(() => session.NotifyDisconnected(originalFault)));
        Exception?[] disposeFailures;
        try
        {
            await terminalPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!session.IsConnected,
                "Fault must publish its terminal before the deterministic cancellation barrier");

            disposeFailures = await Task.WhenAll(
                    CaptureDisposeExceptionAsync(session),
                    CaptureDisposeExceptionAsync(session))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFault.Set();
        }

        var faultFailure = await faultTask.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(faultFailure is null,
            "Fault must not touch a CTS already released by the DisposeAsync owner");
        foreach (var disposeFailure in disposeFailures)
        {
            Ensure(disposeFailure is null,
                "concurrent and repeated DisposeAsync callers must share successful cleanup");
        }
        Ensure(ReferenceEquals(publishedFault, originalFault) && disconnectCount == 1,
            "the original Fault winner must remain the single published terminal");
        Ensure(ReferenceEquals(CaptureSendException(session), originalFault),
            "later operations must keep observing the original fault after CTS cleanup");
        Ensure(transport.DisposeCount == 1,
            "Fault and repeated DisposeAsync calls must dispose their transport exactly once");
    }

    [Test]
    public async Task SynchronousTransportDisposeFailureShouldBecomeOneObservedTask()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transportFailure = new IOException("synchronous transport dispose failed");
        var transport = RpcSessionTestFixture.Transport(
            "synchronous-dispose-failure",
            input.Reader,
            output.Writer,
            DisposeTransport);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());

        session.NotifyDisconnected(new IOException("read failed"));
        var failures = await Task.WhenAll(
            CaptureDisposeExceptionAsync(session),
            CaptureDisposeExceptionAsync(session));

        Ensure(transport.DisposeCount == 1,
            "a synchronous transport dispose throw must still be single-flight");
        foreach (var failure in failures)
        {
            Ensure(ReferenceEquals(failure, transportFailure),
                "the fault observer and every explicit disposal waiter must share the converted faulted task");
        }
        Ensure(!session.IsConnected,
            "a synchronous transport cleanup failure must not reopen the terminal Session");

        ValueTask DisposeTransport() => throw transportFailure;
    }

    [Test]
    public async Task FirstFaultShouldBePublishedOnceAndReusedByLaterSends()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "first-fault",
            input.Reader,
            output.Writer);
        await using var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
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
        Ensure(transport.DisposeCount == 1, "transport should be disposed once");
    }

    [Test]
    public async Task ConcurrentReadAndWriteFaultsShouldConvergeToOneTerminalState()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "concurrent-fault",
            input.Reader,
            output.Writer);
        await using var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
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
        Ensure(transport.DisposeCount == 1, "competing faults should dispose transport once");
    }

    [Test]
    public async Task ConcurrentSendAndDisposeShouldCompletePumpAndReturnCleanly()
    {
        var input = new Pipe();
        var output = new Pipe(new PipeOptions(
            pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024));
        var transport = RpcSessionTestFixture.Transport(
            "send-dispose",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
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

        Ensure(transport.DisposeCount == 1, "concurrent disposal should dispose transport once");
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
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "late-notify",
            input.Reader,
            output.Writer,
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
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "flow-credit-flush",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 4,
            connectionReceiveWindowBytes: 4);
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
        var transport = RpcSessionTestFixture.Transport(
            "throwing-stream-completion",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
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
        Ensure(transport.DisposeCount == 1,
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

    private static async Task<Exception?> CaptureDisposeExceptionAsync(RpcSession session)
    {
        try
        {
            await session.DisposeAsync();
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
