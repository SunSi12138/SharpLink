using Microsoft.Extensions.Logging;
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
    public async Task RunningLifecycleWithConnectionFaultShouldReturnNotReady()
    {
        var transport = new GatedFailingTransportFactory();
        using var loggerFactory = new BlockingSupervisorLoggerFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseLoggerFactory(loggerFactory));

        await client.StartAsync();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.ReleaseFailure();
        await loggerFactory.SupervisorFailureLogged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "connection failure must not fault the local runtime lifecycle");
            Ensure(client.State == SharpLinkConnectionState.Faulted,
                "test must observe the legacy connection Faulted window before supervisor normalization");
            Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
                "connection failure must publish NotReady independently of local lifecycle");

            var result = await client.CheckHealthAsync();

            Ensure(result.Outcome == SharpLinkHealthProbeOutcome.NotReady,
                "Running + connection Faulted + zero Ready connections must be a structured NotReady result");
            Ensure(result.Status is null, "NotReady must not invent a remote status");
        }
        finally
        {
            loggerFactory.Release();
        }
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
    public async Task LocalStopDuringHealthProbeShouldRemainExceptional()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.HealthCheck);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.StartAsync();
        await client.WaitForReadyAsync();

        var probe = client.CheckHealthAsync().AsTask();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.HealthCheck);
        var stop = client.StopAsync().AsTask();

        var exception = await EnsureThrows<SharpLinkException>(probe);
        Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed,
            "local terminal lifecycle must preserve ConnectionClosed instead of returning Unavailable");
        await stop;
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
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
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

    private sealed class GatedFailingTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("expected health lifecycle connection failure");
        }

        internal void ReleaseFailure() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSupervisorLoggerFactory : ILoggerFactory
    {
        private readonly BlockingSupervisorLogger _logger = new();

        internal TaskCompletionSource SupervisorFailureLogged => _logger.SupervisorFailureLogged;

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        internal void Release() => _logger.Release();

        public void Dispose() => Release();
    }

    private sealed class BlockingSupervisorLogger : ILogger
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        internal TaskCompletionSource SupervisorFailureLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.Contains("RunInitialConnectivitySupervisorAsync", StringComparison.Ordinal))
                return;

            SupervisorFailureLogged.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(5));
        }

        internal void Release() => _release.Set();
    }
}
