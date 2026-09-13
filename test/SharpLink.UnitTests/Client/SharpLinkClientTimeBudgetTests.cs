using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientTimeBudgetTests
{
    [Test]
    public async Task ExplicitMethodTimeoutShouldOverrideClientDefaultTimeBudget()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        var method = MethodWithTimeout(TimeSpan.FromSeconds(120));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure((sent.Header.Flags & ProtocolV2FrameFlags.HasTimeBudget) != 0,
            "explicit method timeout should emit a TimeBudget");
        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(120),
            "method timeout must override, not be min-capped by, the 30 second client fallback");

        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "method-timeout override response");
    }

    [Test]
    public async Task InheritedTimeBudgetShouldCapSelectedMethodPolicyWithoutRestartingIt()
    {
        var parentTimeProvider = new ManualTimeProvider();
        var childTimeProvider = new ManualTimeProvider();
        var parentDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(6), parentTimeProvider);
        parentTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            "parent",
            null,
            parentDeadline,
            parentTimeProvider));

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(childTimeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(TimeSpan.FromSeconds(120)),
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(4),
            "a downstream call must propagate the parent's remaining TimeBudget instead of restarting 120 seconds");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "inherited-budget response");
    }

    [Test]
    public async Task InheritedTimeBudgetHandoffDelayShouldNotBeDoubleCounted()
    {
        var timeProvider = new HandoffTimeProvider();
        var parentDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(6), timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            "parent",
            null,
            parentDeadline,
            timeProvider));

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        // ResolveCallControl first samples the local-policy anchor, then observes the shared parent
        // boundary. Advance the one shared monotonic clock on that second read: the parent is now
        // at t=5 with one second left. Preserving the original parent RpcDeadline must retain its
        // t=6 boundary rather than anchoring the remaining second back at logical entry.
        timeProvider.AdvanceOnTimestampRead(2, TimeSpan.FromSeconds(3));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(TimeSpan.FromSeconds(120)),
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(1),
            "the inherited cap must equal the parent's current remaining lifetime, without double-counting the handoff delay");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "inherited handoff response");
    }

    [Test]
    public async Task InheritedSharedClockBoundaryShouldNotBeExtendedByReanchorDelay()
    {
        var timeProvider = new HandoffTimeProvider();
        var parentDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(6), timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
            "parent",
            null,
            parentDeadline,
            timeProvider));

        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(30));
            });
        await client.ConnectAsync();

        // The old projection sampled four seconds of parent lifetime at t=2 and could then be
        // descheduled before taking a fresh child anchor. Advancing on the third timestamp read
        // models that gap: re-anchoring four seconds at t=5 would incorrectly extend the parent
        // to t=9. A shared clock must preserve the original t=6 parent boundary and emit one second.
        timeProvider.AdvanceOnTimestampRead(3, TimeSpan.FromSeconds(3));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            MethodWithTimeout(TimeSpan.FromSeconds(120)),
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);

        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(1),
            "a shared parent deadline must not be extended by a remaining-duration re-anchor gap");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "shared-parent reanchor response");
    }

    [Test]
    public async Task TimedClientStreamShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 288,
            Kind: RpcMethodKind.ClientStreaming,
            HasResponsePayload: true,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var probe = new ProducerProbe();
        var streams = new ProbeClientStreams(probe);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        // Drain all output associated with ConnectAsync before arming the one-shot writer hook.
        // The ownership gate is unchanged: the producer still starts only after the owning
        // Request finished emission. What changed is that emission no longer re-samples or
        // arbitrates the TimeBudget, so the frame carries the remaining sampled at creation.
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        transport.Connection.RunOnNextOutputBufferRequest(
            () => timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5)));
        var invocation = channel.InvokeClientStreamingAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(5),
            "the wire TimeBudget must be the remaining sampled once when the frame is created");
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(!invocation.IsCompleted,
            "starting the client-stream producer must not complete the call on its own");

        // The pending deadline owns the terminal decision now that the send pump publishes
        // frames verbatim, so the elapsed budget fails the call and cancels the remote stream.
        timeProvider.Advance(TimeSpan.Zero);
        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an emitted client-stream Request whose budget elapsed must fail through its pending deadline");
    }

    [Test]
    public async Task DynamicModuleServerStreamDeadlineShouldWinBeforeDeferredModuleDrain()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));

        using var moduleContext = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(timeProvider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest();
        using var registration = moduleContext.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(
            typeof(SharpLinkClientTimeBudgetTests).Assembly,
            manifest,
            registration);
        var channel = new SharpLinkModuleRpcChannel(client, module);
        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 289,
            Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var request = default(RpcEmptyRequest);

        var stream = channel.InvokeServerStreamingAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            client.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default);

        // The dynamic wrapper freezes the logical lifetime at proxy invocation. Let that lifetime
        // expire without running timers, then make the module drain before enumeration. The earlier
        // logical DeadlineExceeded owner must win over the later local module Unavailable state.
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        Ensure(module.TryBeginDraining(), "dynamic module should enter draining for the ordering regression");
        await using var enumerator = stream.GetAsyncEnumerator();
        var failure = await CaptureSharpLinkExceptionAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "dynamic streaming must submit deferred module acquisition to the frozen logical deadline owner first");
        Ensure(!await transport.Connection.TryWaitForSentPacket(ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(50)),
            "an already-expired dynamic stream must not begin a network request at enumeration time");
    }

    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task ClientDefaultTimeoutShouldReachOnlyTheShapesThatOptIn(RpcMethodKind kind)
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRequestTimeout(TimeSpan.FromSeconds(30)));
        await client.ConnectAsync();

        // The benchmark probe declares neither [Timeout] nor a per-call deadline for its one-way and
        // streaming methods while the client runs with a 30 s default. Only the unary entry point
        // resolves that default, so only it may put a budget on the wire.
        var method = MethodWithoutTimeout(kind, 320 + (long)kind);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var noStreams = default(RpcNoClientStreams);
        var probe = new ProducerProbe();
        var producer = new ProbeClientStreams(probe);
        using var cancellation = new CancellationTokenSource();
        var responseCodec = channel.RuntimeContext.Codecs.GetCodec<int>();
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        switch (kind)
        {
            case RpcMethodKind.Unary:
                {
                    var invocation = channel.InvokeUnaryAsync(
                        method,
                        in request,
                        RpcEmptyRequestCodec.Instance,
                        responseCodec,
                        metadata: null,
                        cancellationToken: cancellation.Token);
                    var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
                    Ensure(HasTimeBudget(sent),
                        "the client default must reach a unary method that declares no [Timeout]");
                    Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(30),
                        "the unary Request must carry the client default budget for the benchmark shape");
                    await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
                    await invocation;
                    return;
                }
            case RpcMethodKind.OneWay:
                {
                    var invocation = channel.InvokeOneWayAsync(
                        method,
                        in request,
                        RpcEmptyRequestCodec.Instance,
                        in noStreams,
                        metadata: null,
                        cancellationToken: default).AsTask();
                    var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
                    Ensure(!HasTimeBudget(sent),
                        "the client default must not reach a plain OneWay benchmark method");
                    await invocation;
                    return;
                }
            case RpcMethodKind.ClientStreaming:
                {
                    var invocation = channel.InvokeClientStreamingAsync(
                        method,
                        in request,
                        RpcEmptyRequestCodec.Instance,
                        responseCodec,
                        in producer,
                        metadata: null,
                        cancellationToken: cancellation.Token).AsTask();
                    var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
                    Ensure(!HasTimeBudget(sent),
                        "the client default must not reach a client-streaming benchmark Request");
                    await CancelAndIgnoreAsync(cancellation, invocation);
                    return;
                }
            case RpcMethodKind.ServerStreaming:
                {
                    await using var enumerator = channel.InvokeServerStreamingAsync(
                        method,
                        in request,
                        RpcEmptyRequestCodec.Instance,
                        responseCodec,
                        metadata: null,
                        cancellationToken: cancellation.Token).GetAsyncEnumerator();
                    var moveNext = enumerator.MoveNextAsync().AsTask();
                    var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
                    Ensure(!HasTimeBudget(sent),
                        "the client default must not reach a server-streaming benchmark Request");
                    await CancelAndIgnoreAsync(cancellation, moveNext);
                    return;
                }
            default:
                {
                    await using var enumerator = channel.InvokeDuplexStreamingAsync(
                        method,
                        in request,
                        RpcEmptyRequestCodec.Instance,
                        responseCodec,
                        in producer,
                        metadata: null,
                        cancellationToken: cancellation.Token).GetAsyncEnumerator();
                    var moveNext = enumerator.MoveNextAsync().AsTask();
                    var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
                    Ensure(!HasTimeBudget(sent),
                        "the client default must not reach a duplex benchmark Request");
                    await CancelAndIgnoreAsync(cancellation, moveNext);
                    return;
                }
        }
    }

    [Test]
    public async Task ClientDefaultTimeoutShouldNotEnforceAPlainOneWayDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder
                .UseTimeProvider(timeProvider)
                .UseRequestTimeout(TimeSpan.FromSeconds(30)));
        await client.ConnectAsync();

        var method = MethodWithoutTimeout(RpcMethodKind.OneWay, 326);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);
        using var stall = new ManualResetEventSlim(initialState: false);
        transport.Connection.RunOnNextOutputBufferRequest(() => stall.Wait(TimeSpan.FromSeconds(30)));

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        // An untimed OneWay owns no lifetime, so it returns as soon as its frame is admitted rather
        // than waiting for the transport write. What matters here is that letting the client default
        // elapse neither fails the call nor produces the cancel a live deadline would publish.
        await invocation.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        stall.Set();
        var frames = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(200));
        Ensure(frames.Exists(static frame => frame.Header.Type == ProtocolV2FrameType.Request),
            "an untimed OneWay must still publish its Request");
        Ensure(frames.TrueForAll(static frame => frame.Header.Type is not ProtocolV2FrameType.Cancel),
            "the client default must not turn an untimed OneWay into a deadline that cancels");
    }

    private static bool HasTimeBudget(TestSentFrame sent)
        => (sent.Header.Flags & ProtocolV2FrameFlags.HasTimeBudget) != 0;

    /// <summary>A contract method that declares no <c>[Timeout]</c>, which is what the probe uses.</summary>
    private static RpcMethodDescriptor MethodWithoutTimeout(RpcMethodKind kind, long methodId)
    {
        var hasClientStreams = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming;
        return new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: methodId,
            Kind: kind,
            HasResponsePayload: kind is not RpcMethodKind.OneWay,
            HasClientStreams: hasClientStreams,
            HasMethodTimeout: false,
            MethodTimeout: null,
            ClientStreamCount: hasClientStreams ? 1 : 0);
    }

    /// <summary>
    /// Ends a shape that is still pending on a peer that never answers, and observes the outcome so a
    /// cancelled call cannot keep running after the wire assertion it served.
    /// </summary>
    private static async Task CancelAndIgnoreAsync(CancellationTokenSource cancellation, Task pending)
    {
        await cancellation.CancelAsync();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new Exception("a cancelled call must not keep running");
        }
        catch (Exception)
        {
            // The shape fails with its own cancellation error; this test asserts the wire budget.
        }
    }

    private static RpcMethodDescriptor MethodWithTimeout(TimeSpan timeout)
        => new(
            ContractId: 1,
            MethodId: 287,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: timeout);

    private static TimeSpan ReadTimeBudget(TestSentFrame sent)
        => TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(
            sent.Payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes, sizeof(long))));

    private static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));
        Ensure(connections.Length == 1, "expected exactly one ready connection");
        return connections[0];
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("expected SharpLinkException");
    }

    private sealed class ProducerProbe
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ulong RequestId;
    }

    private readonly struct ProbeClientStreams(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            probe.RequestId = unchecked((ulong)requestId);
            probe.Started.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HandoffTimeProvider : TimeProvider
    {
        private long _timestamp;
        private long _advanceTicks;
        private int _readsUntilAdvance;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _readsUntilAdvance) > 0 &&
                Interlocked.Decrement(ref _readsUntilAdvance) == 0)
            {
                var advanceTicks = Interlocked.Exchange(ref _advanceTicks, 0);
                if (advanceTicks != 0)
                    Interlocked.Add(ref _timestamp, advanceTicks);
            }
            return Volatile.Read(ref _timestamp);
        }

        internal void Advance(TimeSpan elapsed)
            => Interlocked.Add(ref _timestamp, elapsed.Ticks);

        internal void AdvanceOnTimestampRead(int readNumber, TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readNumber);
            Volatile.Write(ref _advanceTicks, elapsed.Ticks);
            Volatile.Write(ref _readsUntilAdvance, readNumber);
        }
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(SharpLinkClientTimeBudgetTests).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x74696d652d627564UL, 0x6765742d74657374UL);
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    /// <summary>Reads every frame the client writes until the transport stays quiet for one period.</summary>
    private static async Task<List<TestSentFrame>> ReadSentFramesAsync(
        TestClientTransportFactory transport,
        TimeSpan quietPeriod)
    {
        var frames = new List<TestSentFrame>();
        while (await transport.Connection.TryReadNextSentFrameAsync(quietPeriod) is { } frame)
            frames.Add(frame);
        return frames;
    }

    private static async Task DrainSentFramesAsync(TestClientTransportFactory transport)
        => _ = await ReadSentFramesAsync(transport, TimeSpan.FromMilliseconds(20));

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
