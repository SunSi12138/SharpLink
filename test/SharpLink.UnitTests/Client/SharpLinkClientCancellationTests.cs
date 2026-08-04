using System.Diagnostics;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientCancellationTests
{
    [Test]
    public async Task InvokeWithDefaultTimeoutNoPayloadAsyncShouldTimeoutAndSendCancel()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(80));

        await client.ConnectAsync();

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var exception = await EnsureThrows<SharpLinkException>(invokeTask);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "timeout should map to DeadlineExceeded");

        var cancelPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Cancel);
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "cancel should target same request");
    }

    [Test]
    public async Task InvokeCancellableNoPayloadAsyncShouldUseOperationCanceledWhenUserTokenCancels()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5));

        await client.ConnectAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            cancellationToken: cts.Token).AsTask();

        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await EnsureThrows<OperationCanceledException>(invokeTask);
        var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel);
        Ensure(cancel.Header.RequestId == request.RequestId, "user cancel request ID");
        Ensure(cancel.Payload is [(byte)ProtocolV2CancelReason.UserCancellation],
            "user cancellation should send its negotiated reason");
    }

    [Test]
    public async Task ReceiveCancelPacketShouldNotBreakPendingRequest()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));

        await client.ConnectAsync();

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, unchecked((long)callPacket.RequestId));
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)callPacket.RequestId));

        var value = await invokeTask;
        Ensure(value == 0, "zero-valued Int32 response");
    }

    [Test]
    public async Task InvokeOneWayNoPayloadShouldNotCreateTimeoutCancel()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(80));

        await client.ConnectAsync();

        await ClientInvokerTestHelper.InvokeOneWayAsync(client);
        var hasCancel = await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Cancel, TimeSpan.FromMilliseconds(200));
        Ensure(!hasCancel, "oneway call should not send timeout cancel");
    }

    [Test]
    public async Task EarlyServerStreamDisposalShouldSendConsumerAbandonedReason()
    {
        using var telemetryListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(telemetryListener);

        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5));

        await client.ConnectAsync();
        var stream = ClientInvokerTestHelper.InvokeServerStreaming(client);
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var enumerator = stream.GetAsyncEnumerator();
        await enumerator.DisposeAsync();

        var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel);
        Ensure(cancel.Header.RequestId == request.RequestId, "consumer abandonment request ID");
        Ensure(cancel.Payload is [(byte)ProtocolV2CancelReason.ConsumerAbandoned],
            "early stream disposal should send ConsumerAbandoned");
    }

    private static async Task<TException> EnsureThrows<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

}
