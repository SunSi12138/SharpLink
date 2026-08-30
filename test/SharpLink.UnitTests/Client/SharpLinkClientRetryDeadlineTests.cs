using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetryDeadlineSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRetryDeadlineTests
{
    [Test]
    public async Task RetryDelayEndingAtTheSharedDeadlineShouldWaitForTerminalArbitration()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var admission = new CountingAdmissionPolicy();
        var client = ClientBuilderTestHelper.BuildEndpoint(
            Endpoint("retry-deadline", 5001), transport, builder =>
            {
                builder.UseTimeProvider(provider);
                ConfigureRetry(builder, RetryOptions(2, TimeSpan.FromSeconds(5)));
                builder.UseEndpointAdmission(admission);
                builder.UseRequestTimeout(TimeSpan.FromSeconds(5));
            });
        try
        {
            await client.ConnectAsync();
            var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();
            var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);

            await InjectErrorAsync(transport, request, SharpLinkErrorCode.Unavailable);
            await Task.Yield();
            Ensure(!invocation.IsCompleted,
                "a future deadline must remain a contender rather than completing the retry wait early");
            Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)),
                "retry backoff must not publish a second request before its deadline");

            provider.Advance(TimeSpan.FromSeconds(5));
            var failure = await EnsureThrows<SharpLinkException>(invocation);

            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "the frozen deadline must terminate the retry wait at its boundary");
            Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request, TimeSpan.FromMilliseconds(100)),
                "deadline completion must not publish a second request");
            Ensure(client.ActiveClientCallCount == 0,
                "deadline completion must release the complete logical invocation");
        }
        finally
        {
            await client.DisposeAsync();
        }

        Ensure(provider.ActiveTimerCount == 0,
            "client shutdown must release the shared scheduler and heartbeat timers");
    }
}
