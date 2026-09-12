using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientHealthTests
{
    [Test]
    [Arguments(SharpLinkHealthStatus.Ready)]
    [Arguments(SharpLinkHealthStatus.Draining)]
    [Arguments(SharpLinkHealthStatus.Unhealthy)]
    public async Task HealthProbeShouldReturnRemoteStatus(SharpLinkHealthStatus status)
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var probe = client.CheckHealthAsync().AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck);
        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.HealthResponse,
            ProtocolV2FrameFlags.None,
            request.RequestId,
            new byte[] { (byte)status });

        var result = await probe;
        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.Success, "remote response must report Success");
        Ensure(result.Status == status, "remote response must preserve the health status");
    }

    [Test]
    public async Task HealthProbeWithoutReadyConnectionShouldReturnNotReady()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);

        var result = await client.CheckHealthAsync();

        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.NotReady, "zero Ready connections must report NotReady");
        Ensure(result.Status is null, "NotReady must not invent a remote health status");
        Ensure(transport.ConnectCount == 0, "health query must not start connectivity as a side effect");
    }

    [Test]
    public async Task HealthProbeShouldReturnUnsupportedWhenCapabilityWasNotNegotiated()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var result = await client.CheckHealthAsync();

        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.Unsupported, "missing capability must report Unsupported");
        Ensure(result.Status is null, "Unsupported must not invent a remote health status");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.HealthCheck,
                TimeSpan.FromMilliseconds(100)),
            "Unsupported peers must not receive a health frame");
    }

    [Test]
    public async Task HealthProbeShouldReturnUnavailableWhenConnectionClosesInFlight()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var probe = client.CheckHealthAsync().AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck);
        await transport.Connection.DisposeAsync();

        var result = await probe;
        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.Unavailable, "connection loss must report Unavailable");
        Ensure(result.Status is null, "Unavailable must not invent a remote health status");
    }

    [Test]
    public async Task HealthProbeCallerCancellationShouldRemainOperationCanceled()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        using var cancellation = new CancellationTokenSource();

        var probe = client.CheckHealthAsync(cancellation.Token).AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck);
        cancellation.Cancel();

        _ = await EnsureThrows<OperationCanceledException>(probe);
    }

    [Test]
    public async Task HealthProbeMalformedResponseShouldPreserveProtocolViolation()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var probe = client.CheckHealthAsync().AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck);
        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.HealthResponse,
            ProtocolV2FrameFlags.None,
            request.RequestId,
            new byte[] { byte.MaxValue });

        var exception = await EnsureThrows<SharpLinkException>(probe);
        Ensure(exception.Code == SharpLinkErrorCode.ProtocolViolation,
            "malformed health responses must remain protocol failures");
    }

    [Test]
    public async Task MultiClusterHealthProbeShouldUseSameStructuredOutcome()
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .AddCluster(
                "orders",
                child => child.UseTransport(new TestClientTransportFactory()))
            .Build();
        await client.ConnectAsync();

        var result = await client.CheckHealthAsync(new SharpLinkClusterKey("orders"));

        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.Unsupported,
            "multi-cluster scoped health must preserve child structured outcomes");
        Ensure(result.Status is null, "multi-cluster Unsupported must not invent a remote status");
    }

    private static async Task<TException> EnsureThrows<TException>(Task task)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new Exception($"expected {typeof(TException).Name}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
