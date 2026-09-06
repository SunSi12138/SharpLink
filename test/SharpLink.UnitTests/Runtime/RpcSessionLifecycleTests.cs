using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

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
    // MeterListener registration is process-wide and this test pauses inside its callback.
    [NotInParallel]
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
    [Arguments("sync", "dispose")]
    [Arguments("async", "dispose")]
    [Arguments("backpressure", "dispose")]
    [Arguments("sync", "fault")]
    [Arguments("async", "fault")]
    [Arguments("backpressure", "fault")]
    public async Task TerminalTransitionDuringSendShouldReturnPublishedFailure(
        string sendPath,
        string terminalPath)
    {
        var provider = new BlockingCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            $"terminal-send-{sendPath}-{terminalPath}",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.Compression,
            compressionBinding: context.Compression.ProviderBindings[0]);
        var published = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCount = 0;
        session.OnDisconnected += exception =>
        {
            Interlocked.Increment(ref publishedCount);
            published.TrySetResult(exception);
        };
        var original = CreateResponsePacket(session, 2048);
        var send = StartSendAsync(session, original, sendPath);
        Exception? terminal = null;
        try
        {
            try
            {
                await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (terminalPath == "fault")
                {
                    session.NotifyDisconnected(
                        new SharpLinkException(SharpLinkErrorCode.DataLoss, "terminal send race"));
                }
                else
                {
                    session.BeginShutdown();
                }
                terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                provider.Release();
            }

            var failure = await send.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(terminal is SharpLinkException, "the terminal transition must publish a structured failure");
            Ensure(ReferenceEquals(terminal, failure), "the in-flight send must observe the published terminal instance");
            Ensure(failure is SharpLinkException { Code: not SharpLinkErrorCode.ProtocolViolation },
                "a terminal transition must not be rewritten as a protocol validation failure");
            Ensure(publishedCount == 1, "the terminal transition must be published exactly once");
            Ensure(!session.IsConnected && session.QueuedSendBytes == 0,
                "the terminal send must not remain connected or strand queued bytes");
            Ensure(transport.DisposeCount == 1, "the terminal send must dispose its transport exactly once");
            EnsureReturned(original, "compression must return the original packet owner");
            Ensure(provider.Candidate is not null, "compression must expose its replacement packet owner");
            EnsureReturned(provider.Candidate!, "terminal validation must return the replacement packet owner");
        }
        finally
        {
            await CleanupSendRaceAsync(provider.Release, send, session);
        }
    }

    [Test]
    [Arguments("sync")]
    [Arguments("async")]
    [Arguments("backpressure")]
    public async Task PumpCreationObservingTerminalShouldReturnValidatedPacket(string sendPath)
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            $"terminal-pump-{sendPath}",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        var published = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDisconnected += exception => published.TrySetResult(exception);
        var packet = new BlockingPacketWriter();
        packet.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, requestId: 1);
        packet.Arm();
        var send = StartSendAsync(session, packet, sendPath);
        Exception? terminal = null;
        try
        {
            try
            {
                await packet.Entered.Task;
                session.BeginShutdown();
                terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                packet.Release();
            }

            var failure = await send.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var returnCount = packet.DisposeCount;
            if (returnCount == 0)
                packet.Dispose();

            Ensure(ReferenceEquals(terminal, failure), "pump creation must preserve the published terminal instance");
            Ensure(returnCount == 1, "a validated packet rejected before pump ownership must be returned exactly once");
            Ensure(session.QueuedSendBytes == 0, "a rejected validated packet must not affect queue accounting");
            Ensure(transport.DisposeCount == 1, "the pump race must dispose its transport exactly once");
        }
        finally
        {
            await CleanupSendRaceAsync(packet.Release, send, session);
        }
    }

    [Test]
    [Arguments("sync")]
    [Arguments("async")]
    [Arguments("backpressure")]
    public async Task ExistingPumpShouldRejectValidatedPacketAfterTerminalWins(string sendPath)
    {
        var input = new Pipe();
        var output = new Pipe();
        var transport = RpcSessionTestFixture.Transport(
            $"terminal-existing-pump-{sendPath}",
            input.Reader,
            output.Writer);
        var session = new RpcSession(transport, RpcSessionTestFixture.ClientOptions());
        RpcSessionTestFixture.CompleteHandshake(session);
        session.SendPacket(CreatePacket(session));
        var published = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseShutdown = new ManualResetEventSlim(initialState: false);
        session.OnDisconnected += exception =>
        {
            published.TrySetResult(exception);
            if (!releaseShutdown.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The in-flight send did not release shutdown.");
        };
        var packet = new BlockingPacketWriter();
        packet.WritePacket(ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, requestId: 2);
        packet.Arm();
        var send = StartSendAsync(session, packet, sendPath);
        Task? shutdown = null;
        Exception? terminal = null;
        Exception? failure = null;
        try
        {
            await packet.Entered.Task;
            shutdown = LongRunningTestWorker.Run(session.BeginShutdown);
            terminal = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
            packet.Release();
            failure = await send.WaitAsync(TimeSpan.FromSeconds(2));
            releaseShutdown.Set();
            await shutdown!.WaitAsync(TimeSpan.FromSeconds(2));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var returnCount = packet.DisposeCount;
            if (returnCount == 0)
                packet.Dispose();

            Ensure(ReferenceEquals(terminal, failure),
                "a published terminal must win before an existing pump accepts the validated packet");
            Ensure(returnCount == 1, "an existing pump must return a terminally rejected packet exactly once");
            Ensure(session.QueuedSendBytes == 0, "terminal rejection must leave existing-pump accounting balanced");
            Ensure(transport.DisposeCount == 1, "existing-pump shutdown must dispose its transport exactly once");
        }
        finally
        {
            await CleanupSendRaceAsync(
                () =>
                {
                    packet.Release();
                    releaseShutdown.Set();
                },
                send,
                session,
                shutdown);
        }
    }

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

    private static IRpcByteBufferWriter CreateResponsePacket(RpcSession session, int payloadBytes)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId: 1))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static Task<Exception?> StartSendAsync(
        RpcSession session,
        IRpcByteBufferWriter packet,
        string sendPath)
        => LongRunningTestWorker.RunAsync(
            async () =>
            {
                try
                {
                    switch (sendPath)
                    {
                        case "sync":
                            session.SendPacket(packet);
                            break;
                        case "async":
                            await session.SendPacketAsync(
                                packet,
                                waitForCapacity: true,
                                forceFlush: false);
                            break;
                        case "backpressure":
                            await session.SendPacketWithBackpressureAsync(packet);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(sendPath),
                                sendPath,
                                "Unknown send path.");
                    }
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });

    private static async Task CleanupSendRaceAsync(
        Action releaseGates,
        Task send,
        RpcSession session,
        Task? shutdown = null)
    {
        releaseGates();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown is not null)
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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

    private sealed class BlockingCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        internal TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IRpcByteBufferWriter? Candidate { get; private set; }
        public string WireProfile => "test-terminal-send-race";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Candidate = (IRpcByteBufferWriter)output;
            Entered.TrySetResult(true);
            if (!_release.Wait(TimeSpan.FromSeconds(5), cancellationToken))
                throw new TimeoutException("The terminal transition did not release compression.");
            var span = output.GetSpan(1);
            span[0] = 0;
            output.Advance(1);
            return true;
        }

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        internal void Release() => _release.Set();
    }

    private sealed class BlockingPacketWriter : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _armed;
        private int _disposeCount;

        internal TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan
        {
            get
            {
                if (Volatile.Read(ref _armed) != 0)
                {
                    Entered.TrySetResult(true);
                    if (!_release.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The terminal transition did not release packet validation.");
                }
                return _inner.WrittenSpan;
            }
        }
        public int Capacity => _inner.Capacity;

        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Clear() => _inner.Clear();
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _inner.Dispose();
        }

        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.Set();
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
