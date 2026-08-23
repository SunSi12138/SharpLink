using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
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
    public async Task InheritedTimeBudgetHandoffDelayShouldConsumeChildLifetime()
    {
        var childTimeProvider = new ManualTimeProvider();
        var parentTimeProvider = new HandoffParentTimeProvider(childTimeProvider);
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

        // Simulate a deschedule/handoff delay precisely while the parent remaining budget is sampled.
        // The child timestamp must already have been anchored, so these three seconds are consumed
        // instead of being re-added after the parent reports four seconds remaining.
        parentTimeProvider.AdvanceChildOnNextTimestampRead(TimeSpan.FromSeconds(3));
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
            "delay between child anchoring and inherited-budget sampling must consume the child lifetime");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "inherited handoff response");
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
        var invocation = channel.InvokeClientStreamingAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        // Advance past the monotonic boundary without running the pending-call timer. The explicit
        // flush then makes the send pump arbitrate expiry at the real emission boundary.
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an initial client-stream Request that expires in the send queue must fail locally");
        Ensure(!probe.Started,
            "the client-stream producer must not start until its owning Request survives emission");
        Ensure(!await transport.Connection.TryWaitForSentPacket(ProtocolV2FrameType.StreamData, TimeSpan.FromMilliseconds(50)),
            "no orphan StreamData may be emitted after the owning Request is dropped");
    }

    [Test]
    public async Task DynamicModuleServerStreamShouldFreezeDeadlineAtProxyInvocation()
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

        // The dynamic wrapper may defer module-lease acquisition to enumeration, but it must not
        // defer the logical RPC lifetime. Delay after the proxy call therefore consumes timeout.
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        await using var enumerator = stream.GetAsyncEnumerator();
        var failure = await CaptureSharpLinkExceptionAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "dynamic streaming must use the deadline frozen when the proxy method was invoked");
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

    private sealed class HandoffParentTimeProvider(ManualTimeProvider childTimeProvider) : TimeProvider
    {
        private long _timestamp;
        private long _childAdvanceTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var childAdvanceTicks = Interlocked.Exchange(ref _childAdvanceTicks, 0);
            if (childAdvanceTicks != 0)
                childTimeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromTicks(childAdvanceTicks));
            return Volatile.Read(ref _timestamp);
        }

        internal void Advance(TimeSpan elapsed)
            => Interlocked.Add(ref _timestamp, elapsed.Ticks);

        internal void AdvanceChildOnNextTimestampRead(TimeSpan elapsed)
            => Volatile.Write(ref _childAdvanceTicks, elapsed.Ticks);
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(SharpLinkClientTimeBudgetTests).Assembly;
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
