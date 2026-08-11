using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientTimeoutTests
{
    [Test]
    public async Task InvokeWithTimeoutNoPayloadAsyncShouldTimeoutAndSendCancel()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(transport);

        await client.ConnectAsync();

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(80) }).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var exception = await EnsureThrows<SharpLinkException>(invokeTask);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "timeout should map to DeadlineExceeded");

        var cancelFrame = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel);
        Ensure(cancelFrame.Header.RequestId == callPacket.RequestId, "cancel should target same request");
        Ensure(cancelFrame.Payload is [(byte)ProtocolV2CancelReason.DeadlineExceeded],
            "deadline timeout should send DeadlineExceeded reason");
    }

    [Test]
    public async Task InvokeCancellableNoPayloadAsyncTimeoutAndUserCancelShouldSendSingleCancel()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(1)));

        await client.ConnectAsync();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(80));

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            cancellationToken: cts.Token).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await EnsureThrows<Exception>(invokeTask);

        var cancelFrame = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel);
        Ensure(cancelFrame.Header.RequestId == callPacket.RequestId, "first cancel should target same request");
        Ensure(cancelFrame.Payload is [(byte)ProtocolV2CancelReason.UserCancellation],
            "user token should win the cancellation race in this test");
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
