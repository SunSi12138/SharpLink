using System.Buffers.Binary;
using System.Reflection;
using System.Threading;
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
    public async Task TimedUnaryShouldPublishCreationTimeBudgetAndFailThroughPendingDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
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

        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        Ensure(ReadTimeBudget(sent) == TimeSpan.FromSeconds(5),
            "the wire TimeBudget must be the remaining sampled once when the frame is created");

        // The send pump no longer drops an expired frame: the Request reaches the transport and
        // the pending deadline scan owns the terminal failure end to end.
        timeProvider.Advance(TimeSpan.Zero);
        var failure = await CaptureSharpLinkExceptionAsync(invocation);
        Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "an emitted Unary whose budget elapsed must fail through its pending deadline");
        var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel);
        Ensure(cancel.Header.RequestId == sent.Header.RequestId,
            "the pending deadline must cancel the already-emitted remote call");
    }

    [Test]
    public async Task TimedUnaryStuckInTheTransportWriteQueueShouldStillFailAtItsDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        using var releaseWrite = new ManualResetEventSlim();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connection.RunOnNextOutputBufferRequest(() =>
        {
            writeEntered.TrySetResult();
            releaseWrite.Wait(TimeSpan.FromSeconds(10));
        });

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 293,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        try
        {
            var invocation = channel.InvokeUnaryAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                metadata: null,
                cancellationToken: default).AsTask();
            await writeEntered.Task;
            Ensure(!invocation.IsCompleted,
                "a Request blocked inside the transport write must still be in flight");

            // The pump cannot publish any further frame while the transport write is blocked, so
            // this failure can only come from the client's own pending deadline.
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(invocation)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "a Request stuck in the transport write queue must still fail at its deadline");
        }
        finally
        {
            releaseWrite.Set();
        }
    }

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
