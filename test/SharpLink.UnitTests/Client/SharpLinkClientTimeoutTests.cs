using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientTimeoutTests
{
    [Test]
    public async Task InvokeWithTimeoutNoPayloadAsyncShouldTimeoutAndSendCancel()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        await client.ConnectAsync();

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(80) }).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var exception = await EnsureThrows<SharpLinkException>(invokeTask);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "timeout should map to DeadlineExceeded");

        var cancelPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Cancel);
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "cancel should target same request");
    }

    [Test]
    public async Task InvokeCancellableNoPayloadAsyncTimeoutAndUserCancelShouldSendSingleCancel()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(80));

        await client.ConnectAsync();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(80));

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            cancellationToken: cts.Token).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await EnsureThrows<Exception>(invokeTask);

        var cancelPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Cancel);
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "first cancel should target same request");
        var hasSecondCancel = await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Cancel, TimeSpan.FromMilliseconds(200));
        Ensure(!hasSecondCancel, "cancel packet should be sent only once");
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
