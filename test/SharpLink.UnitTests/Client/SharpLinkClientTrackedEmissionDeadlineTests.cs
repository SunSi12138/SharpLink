using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientTrackedEmissionDeadlineTests
{
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
        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null,
            cancellationToken: default).AsTask();

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();

        var failure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(5));
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
