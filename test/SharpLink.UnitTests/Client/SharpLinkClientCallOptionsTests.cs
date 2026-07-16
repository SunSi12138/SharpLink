using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientCallOptionsTests
{
    [Test]
    public async Task WaitForReadyFalseShouldFailImmediatelyWhenDisconnected()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var exception = await CaptureSharpLinkException(client.InvokeWithCallOptionsAsync<int>(
            1,
            2,
            payloadWriter: null,
            streamSender: null,
            isOneWay: false,
            hasReturnPayload: true,
            options: default,
            hasMethodTimeout: false,
            methodTimeout: null));
        Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "fail-fast error code");
    }

    [Test]
    public async Task WaitForReadyShouldResumeAfterConnectionBecomesReady()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var invocation = client.InvokeWithCallOptionsAsync<int>(
            1,
            2,
            payloadWriter: null,
            streamSender: null,
            isOneWay: false,
            hasReturnPayload: true,
            options: new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromSeconds(2),
                WaitForReady = true
            },
            hasMethodTimeout: false,
            methodTimeout: null).AsTask();

        await Task.Delay(50);
        Ensure(!invocation.IsCompleted, "call should wait while no connection is ready");
        await client.ConnectAsync();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Response,
            ProtocolV2FrameFlags.None,
            unchecked((long)request.RequestId));

        Ensure(await invocation == 0, "empty response should deserialize to default(int)");
    }

    [Test]
    public async Task WaitForReadyDeadlineShouldMapToDeadlineExceeded()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

        var exception = await CaptureSharpLinkException(client.InvokeWithCallOptionsAsync<int>(
            1,
            2,
            payloadWriter: null,
            streamSender: null,
            isOneWay: false,
            hasReturnPayload: true,
            options: new SharpLinkCallOptions
            {
                Timeout = TimeSpan.FromMilliseconds(80),
                WaitForReady = true
            },
            hasMethodTimeout: false,
            methodTimeout: null));
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "wait deadline error code");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(ValueTask<int> invocation)
    {
        try
        {
            _ = await invocation;
            throw new Exception("expected SharpLinkException");
        }
        catch (SharpLinkException exception)
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
