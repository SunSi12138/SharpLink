using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public partial class RpcSessionLifecycleTests
{

    [Test]
    // MeterListener registration is process-wide and this test pauses inside its callback.
    [NotInParallel]
    public async Task InboundValidationShouldObserveTerminalPublishedBeforeStoppingPhase()
    {
        var connectionBalance = 0L;
        var terminalPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTransition = new ManualResetEventSlim(initialState: false);
        using var listener = new MeterListener();
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
            foreach (var tag in tags)
            {
                if (tag.Key != "rpc.side" || !Equals(tag.Value, "client"))
                    continue;

                Interlocked.Add(ref connectionBalance, measurement);
                if (measurement == -1)
                {
                    // BeginShutdown publishes _terminal before recording the close metric and
                    // transitions the protocol phase only after this callback returns.
                    terminalPublished.TrySetResult();
                    if (!releaseTransition.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Inbound validation did not release the pre-phase terminal barrier.");
                }
                break;
            }
        });
        listener.Start();

        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "terminal-inbound-pre-phase",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var published = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDisconnected += exception => published.TrySetResult(exception);
        session.NotifyConnected();
        Ensure(Volatile.Read(ref connectionBalance) == 1,
            "the pre-phase barrier must observe the Session connection before shutdown");

        var readerObservedConnected = session.IsConnected;
        var shutdown = Task.Run(session.BeginShutdown);
        Exception? validationFailure;
        try
        {
            await terminalPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!session.IsConnected && session.ProtocolPhase == RpcSessionProtocolPhase.Ready,
                "the barrier must pause after terminal publication and before the Stopping phase transition");
            validationFailure = CaptureException(() =>
                session.EnsureInboundFrameAllowed(ProtocolV2FrameType.GoAway));
        }
        finally
        {
            releaseTransition.Set();
        }

        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        var terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(readerObservedConnected,
            "the read side must pass its connected check before the pre-phase terminal winner publishes");
        Ensure(ReferenceEquals(validationFailure, terminal),
            "inbound validation must return the terminal published before the protocol phase changes");
        Ensure(validationFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "the pre-phase shutdown winner must remain a structured ConnectionClosed failure");
        Ensure(transport.DisposeCount == 1 && session.QueuedSendBytes == 0,
            "the pre-phase terminal race must release transport and queue ownership exactly once");
        Ensure(Volatile.Read(ref connectionBalance) == 0,
            "the pre-phase terminal race must leave the connection metric balanced");
    }


    [Test]
    // MeterListener registration is process-wide and this test owns the connection-balance window.
    [NotInParallel]
    [Arguments("dispose")]
    [Arguments("fault")]
    public async Task InboundValidationAfterConnectedCheckAndTerminalPublicationShouldReturnWinner(
        string terminalPath)
    {
        var connectionBalance = 0L;
        using var listener = new MeterListener();
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
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && Equals(tag.Value, "client"))
                {
                    Interlocked.Add(ref connectionBalance, measurement);
                    break;
                }
            }
        });
        listener.Start();

        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            $"terminal-inbound-{terminalPath}",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var originalFault = new SharpLinkException(SharpLinkErrorCode.DataLoss, "terminal inbound race");
        var published = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTransition = new ManualResetEventSlim(initialState: false);
        var publishedCount = 0;
        session.OnDisconnected += exception =>
        {
            Interlocked.Increment(ref publishedCount);
            published.TrySetResult(exception);
            if (!releaseTransition.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Inbound validation did not release the terminal transition.");
        };
        session.NotifyConnected();
        Ensure(Volatile.Read(ref connectionBalance) == 1,
            "the metric listener must observe the Session connection before testing terminal balance");

        var readerObservedConnected = session.IsConnected;
        var transition = Task.Run(() =>
        {
            if (terminalPath == "fault")
                session.NotifyDisconnected(originalFault);
            else
                session.BeginShutdown();
        });
        Exception? terminal;
        Exception? validationFailure;
        try
        {
            terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            validationFailure = CaptureException(() =>
                session.EnsureInboundFrameAllowed(ProtocolV2FrameType.GoAway));
        }
        finally
        {
            releaseTransition.Set();
        }

        var transitionFailure = await CaptureExceptionAsync(
            transition.WaitAsync(TimeSpan.FromSeconds(2)));
        var phaseBeforeDispose = session.ProtocolPhase;
        var disposeFailure = await CaptureDisposeExceptionAsync(session);

        Ensure(readerObservedConnected,
            "the read side must pass its connected check before the terminal transition wins");
        Ensure(terminal is SharpLinkException,
            "the terminal transition must publish a structured failure before inbound validation resumes");
        Ensure(ReferenceEquals(terminal, validationFailure),
            "inbound validation must return the exact published terminal winner");
        Ensure(validationFailure is SharpLinkException { Code: not SharpLinkErrorCode.ProtocolViolation },
            "terminal inbound validation must not synthesize a protocol violation");
        Ensure(terminalPath == "fault"
                ? ReferenceEquals(validationFailure, originalFault) &&
                  phaseBeforeDispose == RpcSessionProtocolPhase.Terminal
                : validationFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed } &&
                  phaseBeforeDispose == RpcSessionProtocolPhase.Stopping,
            "fault must preserve its original Terminal winner while dispose must preserve its Stopping winner");
        Ensure(transitionFailure is null && disposeFailure is null,
            "terminal transition and disposal must complete without a secondary cleanup failure");
        Ensure(publishedCount == 1 && !session.IsConnected,
            "the terminal winner must disconnect the Session exactly once");
        Ensure(transport.DisposeCount == 1 && session.QueuedSendBytes == 0,
            "terminal inbound validation must leave transport and send-queue ownership balanced");
        Ensure(Volatile.Read(ref connectionBalance) == 0,
            "terminal inbound validation must close the connection metric it opened");
    }


    [Test]
    public async Task OneHundredInboundValidationShutdownRacesShouldNeverLeakProtocolViolation()
    {
        var readyWins = 0;
        var terminalWins = 0;
        var protocolViolations = 0;
        var unexpectedFailures = 0;
        var invalidTerminals = 0;
        var disposedTransports = 0;

        for (var round = 0; round < 100; round++)
        {
            var input = new Pipe();
            var output = new Pipe();
            var transport = RpcSessionTestFixture.Transport(
                $"terminal-inbound-race-{round}",
                input.Reader,
                output.Writer);
            var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
            RpcSessionTestFixture.CompleteHandshake(session);
            var published = new TaskCompletionSource<Exception?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnDisconnected += exception => published.TrySetResult(exception);
            using var start = new ManualResetEventSlim(initialState: false);
            var validation = Task.Run(() =>
            {
                start.Wait();
                return CaptureException(() =>
                    session.EnsureInboundFrameAllowed(ProtocolV2FrameType.GoAway));
            });
            var shutdown = Task.Run(() =>
            {
                start.Wait();
                session.BeginShutdown();
            });

            start.Set();
            var validationFailure = await validation.WaitAsync(TimeSpan.FromSeconds(2));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            var terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (terminal is not SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed })
                invalidTerminals++;

            if (validationFailure is null)
            {
                readyWins++;
            }
            else if (validationFailure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation })
            {
                protocolViolations++;
            }
            else if (ReferenceEquals(validationFailure, terminal))
            {
                terminalWins++;
            }
            else
            {
                unexpectedFailures++;
            }

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            disposedTransports += transport.DisposeCount;
        }

        Ensure(protocolViolations == 0,
            "100 shutdown races must not expose the terminal protocol phase as ProtocolViolation");
        Ensure(unexpectedFailures == 0,
            "every losing inbound validation must observe the published terminal instance");
        Ensure(invalidTerminals == 0,
            "every shutdown race must publish a structured ConnectionClosed terminal");
        Ensure(readyWins + terminalWins == 100,
            "every race must linearize as either a valid Ready read or the terminal winner");
        Ensure(disposedTransports == 100,
            "every race round must dispose its independently owned transport exactly once");
    }


    [Test]
    public async Task HealthySessionShouldPreserveInboundProtocolViolation()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "healthy-inbound-protocol-validation",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);

        session.EnsureInboundFrameAllowed(ProtocolV2FrameType.GoAway);
        var failure = CaptureException(() =>
            session.EnsureInboundFrameAllowed(ProtocolV2FrameType.HandshakeRequest));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "a genuinely invalid inbound frame on a healthy Session must remain a protocol violation");
        Ensure(failure?.Message.Contains("Ready", StringComparison.Ordinal) == true,
            "healthy inbound validation must identify the active protocol phase");
        Ensure(session.IsConnected && session.ProtocolPhase == RpcSessionProtocolPhase.Ready,
            "local inbound validation must not terminate or mutate a healthy Session");
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(transport.DisposeCount == 1,
            "healthy inbound validation cleanup must dispose its transport exactly once");
    }


    [Test]
    [NotInParallel]
    public async Task HealthySessionShouldPreserveOutboundProtocolViolation()
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            "healthy-protocol-validation",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var packet = session.RuntimeContext.Buffers.Rent();
        packet.WritePacket(ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, requestId: 1);

        var failure = CaptureException(() => session.SendPacket(packet));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "a genuinely invalid outbound frame on a healthy session must remain a protocol violation");
        Ensure(session.IsConnected, "local outbound validation must not terminate a healthy session");
        EnsureReturned(packet, "outbound validation must return the rejected packet owner");
        await session.DisposeAsync();
        Ensure(transport.DisposeCount == 1, "healthy-session cleanup must dispose its transport exactly once");
    }


    [Test]
    // MeterListener registration is process-wide and this test owns the connection-balance window.
    [NotInParallel]
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
}
