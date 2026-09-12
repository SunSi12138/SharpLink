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
    public async Task TimedClientStreamShouldNotStartProducerUntilRequestSurvivesEmission()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.UseRpcSessionFlush(1024 * 1024, TimeSpan.FromSeconds(10));
            });
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
        // Advancing the manual clock from the target Request's output-buffer acquisition makes
        // the send pump arbitrate expiry at the real emission boundary without racing registration.
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

        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an initial client-stream Request that expires in the send queue must fail locally");
        Ensure(!probe.Started,
            "the client-stream producer must not start until its owning Request survives emission");
        Ensure(!await transport.Connection.TryWaitForSentPacket(ProtocolV2FrameType.StreamData, TimeSpan.FromMilliseconds(50)),
            "no orphan StreamData may be emitted after the owning Request is dropped");
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
        internal bool Started;
    }

    private readonly struct ProbeClientStreams(ProducerProbe probe) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            probe.Started = true;
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
