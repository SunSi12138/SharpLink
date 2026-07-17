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
        var transport = new TestClientTransportFactory();
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

        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await EnsureThrows<OperationCanceledException>(invokeTask);
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
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, unchecked((long)callPacket.RequestId));

        var value = await invokeTask;
        Ensure(value == 0, "empty response should deserialize to default(int)");
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
