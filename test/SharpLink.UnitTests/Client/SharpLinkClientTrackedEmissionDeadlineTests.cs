using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientTrackedEmissionDeadlineTests
{
    [Test]
    public async Task TimedUnaryShouldObserveEmissionWithinTheSendPumpLifetime()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        var trackedBefore = client.FrameworkTaskSnapshotForDiagnostics.TotalTracked;

        using var releaseEmission = new ManualResetEventSlim();
        var emissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connection.RunOnNextOutputBufferRequest(() =>
        {
            emissionEntered.TrySetResult();
            if (!releaseEmission.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test did not release request emission");
        });

        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var method = new RpcMethodDescriptor(
            ContractId: 1, MethodId: 292, Kind: RpcMethodKind.Unary,
            HasResponsePayload: true, HasClientStreams: false,
            HasMethodTimeout: true, MethodTimeout: TimeSpan.FromSeconds(5));
        try
        {
            var invocation = channel.InvokeUnaryAsync(
                method, in request, RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(), metadata: null).AsTask();
            await emissionEntered.Task;
            Ensure(!invocation.IsCompleted,
                "a request blocked before emission must still await its response");
            Ensure(client.FrameworkTaskSnapshotForDiagnostics.TotalTracked == trackedBefore,
                "each timed Unary must use the existing send-pump owner without registering a per-call task");

            releaseEmission.Set();
            var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
            Ensure(!invocation.IsCompleted,
                "successful emission alone must not complete a Unary response operation");
            await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
            Ensure(await invocation == 0, "the emitted request must receive its original response");
        }
        finally
        {
            releaseEmission.Set();
        }
    }

    [Test]
    public async Task TimedUnaryDroppedAtEmissionShouldCompleteWithoutDeadlineTimerCallback()
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
            MethodId: 291,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);

        // Drain output already owned by ConnectAsync (notably the first heartbeat Ping) before
        // arming the one-shot writer hook. The next output-buffer request is then owned by this
        // Unary, so the clock advance occurs at the target Request's actual emission boundary.
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        transport.Connection.RunOnNextOutputBufferRequest(() =>
            timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5)));
        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();

        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "a tracked Unary Request dropped at emission must complete its pending call immediately");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "an expired Unary Request must not reach the transport");
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
