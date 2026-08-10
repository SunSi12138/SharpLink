using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class NegotiatedSessionOptionsTests
{
    [Test]
    public async Task HandshakeCompletionShouldPublishOneCompleteImmutableSnapshot()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options =>
            {
                options.Protocol.MaxFramePayloadBytes = 8192;
                options.FlowControl.StreamReceiveWindowBytes = 4096;
                options.FlowControl.ConnectionReceiveWindowBytes = 8192;
                options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "complete-negotiation",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        var binding = context.Compression.ProviderBindings[0];
        var proposed = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Metadata |
            ProtocolV2Capabilities.Compression |
            ProtocolV2Capabilities.FlowControl,
            4096,
            2048,
            4096,
            binding);

        var completed = session.TryCompleteHandshake(proposed);
        var published = session.NegotiatedOptions;

        Ensure(completed, "the first valid handshake completion must win");
        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Ready,
            "a complete negotiated snapshot must become visible with Ready");
        Ensure(published is not null,
            "Ready must never be observable without negotiated options");
        Ensure(published!.ProtocolMinorVersion == ProtocolV2Constants.MinorVersion &&
               published.Capabilities == proposed.Capabilities &&
               published.MaxFramePayloadBytes == 4096 &&
               published.StreamReceiveWindowBytes == 2048 &&
               published.ConnectionReceiveWindowBytes == 4096,
            "the published snapshot must contain every negotiated scalar from one completion");
        Ensure(published.CompressionBinding == binding && session.HasStreamFlowControl,
            "compression and flow-control bindings must be prepared before publication");
        Ensure(typeof(NegotiatedSessionOptions).GetProperties(
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               .All(static property => property.SetMethod is null),
            "negotiated options must expose no mutable property setters");
    }

    [Test]
    public async Task ConcurrentHandshakeCompletionShouldHaveOneWinnerAndOneSnapshot()
    {
        for (var round = 0; round < 100; round++)
        {
            var input = new Pipe();
            var output = new Pipe();
            await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                $"handshake-race-{round}",
                input.Reader,
                output.Writer,
                RpcSessionTestFixture.ClientOptions(),
                completeHandshake: false);
            var first = CreateOptions(ProtocolV2Capabilities.Metadata, 4096);
            var second = CreateOptions(ProtocolV2Capabilities.HealthCheck, 8192);
            using var start = new ManualResetEventSlim();

            var firstCompletion = Task.Run(() =>
            {
                start.Wait();
                return session.TryCompleteHandshake(first);
            });
            var secondCompletion = Task.Run(() =>
            {
                start.Wait();
                return session.TryCompleteHandshake(second);
            });
            start.Set();
            var results = await Task.WhenAll(firstCompletion, secondCompletion);
            var published = session.NegotiatedOptions;

            Ensure(results.Count(static result => result) == 1,
                $"round {round}: exactly one concurrent completion must win");
            Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Ready && published is not null,
                $"round {round}: the winner must atomically publish Ready and one snapshot");
            Ensure((published!.Capabilities == first.Capabilities &&
                    published.MaxFramePayloadBytes == first.MaxFramePayloadBytes) ^
                   (published.Capabilities == second.Capabilities &&
                    published.MaxFramePayloadBytes == second.MaxFramePayloadBytes),
                $"round {round}: the published snapshot must contain exactly one winning proposal");
        }
    }

    [Test]
    public async Task InvalidHandshakeOptionsShouldFaultWithoutPublishingReady()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options =>
            {
                options.Protocol.MaxFramePayloadBytes = 8192;
                options.FlowControl.StreamReceiveWindowBytes = 4096;
                options.FlowControl.ConnectionReceiveWindowBytes = 8192;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        var invalid = new[]
        {
            new NegotiatedSessionOptions(checked((ushort)(ProtocolV2Constants.MinorVersion + 1)),
                ProtocolV2Capabilities.None, 4096, 2048, 4096),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                (ProtocolV2Capabilities)(1UL << 63), 4096, 2048, 4096),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None, SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1, 2048, 4096),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None, 8193, 2048, 4096),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.FlowControl, 4096, 0, 4096),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.FlowControl, 4096, 4096, 2048),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.FlowControl, 4096, 4097, 8192),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.FlowControl, 4096, 4096, 8193),
            new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.Compression, 4096, 2048, 4096)
        };

        for (var index = 0; index < invalid.Length; index++)
        {
            var input = new Pipe();
            var output = new Pipe();
            await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                $"invalid-handshake-{index}",
                input.Reader,
                output.Writer,
                RpcSessionTestFixture.ClientOptions(context),
                completeHandshake: false);

            var failure = CaptureSharpLinkException(() => session.TryCompleteHandshake(invalid[index]));

            Ensure(failure.Code == SharpLinkErrorCode.ProtocolViolation,
                $"invalid case {index}: negotiation validation must use ProtocolViolation");
            Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Terminal,
                $"invalid case {index}: malformed negotiation must terminate the session");
            Ensure(session.NegotiatedOptions is null && !session.IsConnected,
                $"invalid case {index}: failed negotiation must never publish Ready options");
        }
    }

    [Test]
    public async Task HandshakeCompletionAfterTerminalShouldBeRejected()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "terminal-before-handshake",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(),
            completeHandshake: false);

        session.NotifyDisconnected(new IOException("transport failed"));
        var completed = session.TryCompleteHandshake(CreateOptions(ProtocolV2Capabilities.None, 4096));

        Ensure(!completed, "a terminal session must reject late handshake publication");
        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Terminal &&
               session.NegotiatedOptions is null,
            "a rejected late completion must preserve the terminal state without a snapshot");
    }

    [Test]
    public async Task HandshakeCompletionAndTerminalRaceShouldNeverPublishPartialState()
    {
        for (var round = 0; round < 100; round++)
        {
            var input = new Pipe();
            var output = new Pipe();
            await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                $"handshake-terminal-race-{round}",
                input.Reader,
                output.Writer,
                RpcSessionTestFixture.ClientOptions(),
                completeHandshake: false);
            var options = CreateOptions(ProtocolV2Capabilities.Metadata, 4096);
            using var start = new ManualResetEventSlim();

            var completion = Task.Run(() =>
            {
                start.Wait();
                return session.TryCompleteHandshake(options);
            });
            var termination = Task.Run(() =>
            {
                start.Wait();
                session.NotifyDisconnected(new IOException($"terminal-{round}"));
            });
            start.Set();
            await termination;
            var completed = await completion;
            var published = session.NegotiatedOptions;

            Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Terminal && !session.IsConnected,
                $"round {round}: terminal must win the final lifecycle state");
            Ensure(completed == ReferenceEquals(published, options),
                $"round {round}: a snapshot may exist only when the atomic Ready publication won first");
        }
    }

    [Test]
    public async Task ForeignCompressionBindingShouldFaultWithoutPublishingReady()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        using var ownerContext = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build(includeGeneratedAssemblyCatalog: false);
        using var foreignContext = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()))
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "foreign-compression-binding",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(ownerContext),
            completeHandshake: false);
        var options = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Compression,
            ownerContext.Protocol.MaxFramePayloadBytes,
            ownerContext.FlowControl.StreamReceiveWindowBytes,
            ownerContext.FlowControl.ConnectionReceiveWindowBytes,
            foreignContext.Compression.ProviderBindings[0]);

        var failure = CaptureSharpLinkException(() => session.TryCompleteHandshake(options));

        Ensure(failure.Code == SharpLinkErrorCode.ProtocolViolation,
            "a compression binding owned by another Context must be a protocol failure");
        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Terminal &&
               session.NegotiatedOptions is null,
            "foreign compression initialization must not expose a partial Ready snapshot");

        var mismatchInput = new Pipe();
        var mismatchOutput = new Pipe();
        await using var mismatchSession = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "mismatched-compression-binding",
            mismatchInput.Reader,
            mismatchOutput.Writer,
            RpcSessionTestFixture.ClientOptions(ownerContext),
            completeHandshake: false);
        var mismatch = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.Compression,
            ownerContext.Protocol.MaxFramePayloadBytes,
            ownerContext.FlowControl.StreamReceiveWindowBytes,
            ownerContext.FlowControl.ConnectionReceiveWindowBytes,
            new SharpLinkCompressionProviderBinding("not-brotli", provider));

        var mismatchFailure = CaptureSharpLinkException(() =>
            mismatchSession.TryCompleteHandshake(mismatch));

        Ensure(mismatchFailure.Code == SharpLinkErrorCode.ProtocolViolation &&
               mismatchSession.ProtocolPhase == RpcSessionProtocolPhase.Terminal &&
               mismatchSession.NegotiatedOptions is null,
            "a provider/profile mismatch must terminate before publishing negotiated compression");
    }

    [Test]
    public void ProtocolPhaseFrameMatrixShouldMatchLifecycleRules()
    {
        var handshaking = new[]
        {
            ProtocolV2FrameType.HandshakeRequest,
            ProtocolV2FrameType.HandshakeResponse
        };
        var draining = new[]
        {
            ProtocolV2FrameType.Ping,
            ProtocolV2FrameType.Pong,
            ProtocolV2FrameType.Response,
            ProtocolV2FrameType.Cancel,
            ProtocolV2FrameType.StreamData,
            ProtocolV2FrameType.StreamComplete,
            ProtocolV2FrameType.WindowUpdate,
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameType.HealthResponse
        };

        foreach (var frameType in Enum.GetValues<ProtocolV2FrameType>())
        {
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Handshaking, frameType) ==
                   handshaking.Contains(frameType),
                $"Handshaking frame eligibility mismatch for {frameType}");
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Ready, frameType) ==
                   !handshaking.Contains(frameType),
                $"Ready frame eligibility mismatch for {frameType}");
            Ensure(RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Draining, frameType) ==
                   draining.Contains(frameType),
                $"Draining frame eligibility mismatch for {frameType}");
            Ensure(!RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Stopping, frameType) &&
                   !RpcSessionProtocolRules.IsFrameAllowed(RpcSessionProtocolPhase.Terminal, frameType),
                $"cleanup phases must reject {frameType}");
        }
        var unknown = (ProtocolV2FrameType)byte.MaxValue;
        foreach (var phase in Enum.GetValues<RpcSessionProtocolPhase>())
        {
            Ensure(!RpcSessionProtocolRules.IsFrameAllowed(phase, unknown),
                $"{phase} must reject unknown frame type bytes");
        }
    }

    [Test]
    public async Task DrainingShouldRejectNewRequestsAndPreserveExistingCallFrames()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "draining-frame-matrix",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions());
        session.AssertStateInvariant();

        session.MarkDraining();
        session.AssertStateInvariant();
        var rejection = CaptureSharpLinkException(() =>
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Request)));
        session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response));
        session.SendPacket(CreateFrame(session, ProtocolV2FrameType.StreamData));
        session.SendPacket(CreateFrame(session, ProtocolV2FrameType.WindowUpdate));
        session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Cancel));
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Draining && session.IsDraining,
            "MarkDraining must transition a Ready session exactly once");
        Ensure(rejection.Code == SharpLinkErrorCode.Unavailable,
            "new calls must be rejected with Unavailable while draining");
        Ensure(RpcSessionProtocolRules.IsFrameAllowed(session.ProtocolPhase, ProtocolV2FrameType.Response) &&
               RpcSessionProtocolRules.IsFrameAllowed(session.ProtocolPhase, ProtocolV2FrameType.StreamData) &&
               RpcSessionProtocolRules.IsFrameAllowed(session.ProtocolPhase, ProtocolV2FrameType.WindowUpdate) &&
               RpcSessionProtocolRules.IsFrameAllowed(session.ProtocolPhase, ProtocolV2FrameType.Cancel) &&
               !RpcSessionProtocolRules.IsFrameAllowed(session.ProtocolPhase, ProtocolV2FrameType.Request),
            "draining must preserve existing-call control/data frames while blocking new Request frames");
    }

    [Test]
    public async Task StoppingShouldPublishReceiveTerminationAndStopAnExistingSendPump()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "stopping-accounting-invariant",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions());
        var firstDispatcher = new CompletionRecordingDispatcher();
        session.StreamManager.Register(71, firstDispatcher);

        // Create a real pump before stopping so the invariant must verify its stop request.
        session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response));
        session.BeginShutdown();
        session.AssertStateInvariant();

        var manager = (StreamManager)session.StreamManager;
        Ensure(session.ProtocolPhase == RpcSessionProtocolPhase.Stopping &&
               !session.CanAcceptCalls,
            "BeginShutdown must publish a non-admitting stopping Session");
        Ensure(manager.IsTerminated && manager.ActiveStreamCount == 0,
            "stopping must publish stream termination and release every business-stream count");
        Ensure(firstDispatcher.CompleteCount == 1 &&
               firstDispatcher.LastException is SharpLinkException
               {
                   Code: SharpLinkErrorCode.ConnectionClosed
               },
            "stopping must complete an already-registered receive stream with the terminal reason");

        var lateDispatcher = new CompletionRecordingDispatcher();
        manager.Register(72, lateDispatcher);
        Ensure(lateDispatcher.CompleteCount == 1 && manager.ActiveStreamCount == 0,
            "a late stream registration must observe the published terminal state without incrementing accounting");
        var rejection = CaptureSharpLinkException(() =>
            session.SendPacket(CreateFrame(session, ProtocolV2FrameType.Response)));
        Ensure(rejection.Code == SharpLinkErrorCode.ConnectionClosed,
            "the stopped send pump/session must reject a new outbound frame with the terminal reason");
    }

    [Test]
    public async Task SendPumpShouldEnforceProtocolPhaseBeforeQueueing()
    {
        var handshakeInput = new Pipe();
        var handshakeOutput = new Pipe();
        await using var handshaking = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "handshaking-send-gate",
            handshakeInput.Reader,
            handshakeOutput.Writer,
            RpcSessionTestFixture.ClientOptions(),
            completeHandshake: false);
        var handshakeFailure = CaptureSharpLinkException(() =>
            handshaking.SendPacket(CreateFrame(handshaking, ProtocolV2FrameType.Request)));

        var readyInput = new Pipe();
        var readyOutput = new Pipe();
        await using var ready = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "ready-send-gate",
            readyInput.Reader,
            readyOutput.Writer,
            RpcSessionTestFixture.ClientOptions());
        var readyFailure = CaptureSharpLinkException(() =>
            ready.SendPacket(CreateFrame(ready, ProtocolV2FrameType.HandshakeRequest)));
        ready.MarkDraining();
        var drainingFailure = CaptureSharpLinkException(() =>
            ready.SendPacket(CreateFrame(ready, ProtocolV2FrameType.Request)));
        ready.SendPacket(CreateFrame(ready, ProtocolV2FrameType.Response));
        await ready.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(handshakeFailure.Code == SharpLinkErrorCode.ProtocolViolation &&
               readyFailure.Code == SharpLinkErrorCode.ProtocolViolation,
            "business-before-handshake and handshake-after-Ready must be rejected before enqueue");
        Ensure(drainingFailure.Code == SharpLinkErrorCode.Unavailable,
            "a new outbound Request during draining must use the stable Unavailable classification");
        Ensure(handshaking.QueuedSendBytes == 0 && ready.QueuedSendBytes == 0,
            "phase-rejected frames must not remain in the send queue and allowed cleanup frames must flush");
    }

    private static NegotiatedSessionOptions CreateOptions(
        ProtocolV2Capabilities capabilities,
        int maxFramePayloadBytes)
        => new(
            ProtocolV2Constants.MinorVersion,
            capabilities,
            maxFramePayloadBytes,
            RpcSessionTestFixture.RuntimeContext.FlowControl.StreamReceiveWindowBytes,
            RpcSessionTestFixture.RuntimeContext.FlowControl.ConnectionReceiveWindowBytes);

    private static IRpcByteBufferWriter CreateFrame(
        RpcSession session,
        ProtocolV2FrameType frameType)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        writer.WritePacket(frameType, ProtocolV2FrameFlags.None, 1);
        return writer;
    }

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
            throw new Exception("the operation should throw a SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private sealed class CompletionRecordingDispatcher : IStreamDispatcher
    {
        internal int CompleteCount { get; private set; }
        internal Exception? LastException { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError
                ? new SharpLinkException(
                    SharpLinkErrorCode.RemoteError,
                    string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage)
                : null);

        public void Complete(Exception? exception)
        {
            CompleteCount++;
            LastException = exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
