using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientOneWayTimeBudgetTests
{
    [Test]
    public async Task TimedOneWayClientStreamShouldNotStartProducerUntilRequestSurvivesEmission()
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
            MethodId: 290,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var probe = new ProducerProbe();
        var streams = new ProbeClientStreams(probe);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        // Drain all output associated with ConnectAsync before arming the one-shot writer hook.
        // The next output-buffer request is then owned by this RPC, so the manual-clock advance
        // occurs at the target Request's actual emission boundary instead of racing prior output.
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        transport.Connection.RunOnNextOutputBufferRequest(
            () => timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5)));
        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an initial OneWay client-stream Request that expires at the emission boundary must fail locally");
        Ensure(!probe.Started,
            "the OneWay client-stream producer must not start until its owning Request survives emission");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.StreamData,
                TimeSpan.FromMilliseconds(50)),
            "no orphan OneWay StreamData may be emitted after the owning Request is dropped");
    }

    [Test]
    public async Task TimedOneWayClientStreamShouldFailWhenTheDeadlineElapsesDuringTheProducer()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 291,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new CancellationObservingClientStreams(producerStarted);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The producer is still running when the logical deadline claims the pending call. The
        // claim cancels the producer, so the producer fails locally with an
        // OperationCanceledException while the pending call is already terminal. The deadline must
        // stay the result the caller observes, and observing the pooled lease operation a second
        // time used to leave the invocation hung forever.
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a deadline that claims the call during producer execution must stay the terminal result even when the cancelled producer fails locally");
    }

    [Test]
    public async Task OneWayClientStreamShouldSurfaceConnectionClosedWhenTheProducerFailsAfterTheConnectionDies()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = CreateClientStreamingOneWayMethod(MethodId: 292);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new CancellationObservingClientStreams(producerStarted);
        var channel = (IRpcChannel)client;
        var connection = GetOnlyReadyConnection(client);
        var request = default(RpcEmptyRequest);

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();
        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The connection dies while the producer is running. That terminal claim cancels the
        // producer, which then fails with its own OperationCanceledException. The caller must
        // observe the authoritative ConnectionClosed terminal, not the producer's local failure.
        connection.PendingCalls.FailAllPendingRequests(new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "The owning RPC connection closed while the oneway producer was running."));

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure.Code == SharpLinkErrorCode.ConnectionClosed,
            "a connection close that wins while the producer runs must stay the terminal result");
    }

    [Test]
    public async Task OneWayClientStreamShouldSurfaceCallerCancellationWhenTheProducerFailsAfterTheCallerCancels()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = CreateClientStreamingOneWayMethod(MethodId: 293);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new CancellationObservingClientStreams(producerStarted);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        using var cancellation = new CancellationTokenSource();

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: cancellation.Token).AsTask();
        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The caller cancels while the producer is running: the pending call owns UserCancellation
        // and cancels the producer, whose local failure must not replace the cancellation.
        cancellation.Cancel();

        var exception = await CaptureExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(exception is OperationCanceledException,
            "a caller cancellation that wins while the producer runs must stay the terminal result");
    }

    [Test]
    public async Task OneWayClientStreamShouldSurfaceTheProducerFailure()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = CreateClientStreamingOneWayMethod(MethodId: 294);
        var producerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerFailure = new InvalidOperationException("the oneway producer failed");
        var streams = new FaultingClientStreams(producerStarted, producerFailure);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        var invocation = channel.InvokeOneWayAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default).AsTask();

        // Nothing else claimed the call, so the producer failure is the terminal result and the
        // pooled lease operation must be observed (and returned to the pool) exactly once.
        var exception = await CaptureExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(ReferenceEquals(exception, producerFailure),
            "a producer failure that wins must reach the caller unchanged");
        await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static RpcMethodDescriptor CreateClientStreamingOneWayMethod(long MethodId)
        => new(
            ContractId: 1,
            MethodId: MethodId,
            Kind: RpcMethodKind.OneWay,
            HasResponsePayload: false,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5),
            ClientStreamCount: 1);

    private static async Task<Exception> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Exception("expected the invocation to fail");
    }

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

    /// <summary>
    /// A oneway client-stream producer that runs until the pending call cancels it. The cancellation
    /// token is what the pending table signals, so the producer fails locally with an
    /// <see cref="OperationCanceledException"/> exactly like a real gated producer would.
    /// </summary>
    private readonly struct CancellationObservingClientStreams(TaskCompletionSource started) : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly struct FaultingClientStreams(
        TaskCompletionSource started,
        Exception failure) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(
            IRpcClientStreamSink sink,
            long requestId,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            return ValueTask.FromException(failure);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
