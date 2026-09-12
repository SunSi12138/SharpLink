using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkRemoteHealthCheckTests
{
    [Test]
    public async Task RemoteHealthCheckShouldMapStructuredResults()
    {
        var cases = new (SharpLinkHealthCheckResult Result, HealthStatus Expected)[]
        {
            (new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready), HealthStatus.Healthy),
            (new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining), HealthStatus.Degraded),
            (new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Unhealthy), HealthStatus.Unhealthy),
            (SharpLinkHealthCheckResult.NotReady, HealthStatus.Unhealthy),
            (SharpLinkHealthCheckResult.Unavailable, HealthStatus.Unhealthy),
            (SharpLinkHealthCheckResult.Unsupported, HealthStatus.Unhealthy)
        };

        foreach (var testCase in cases)
        {
            var check = new SharpLinkRemoteHealthCheck(
                new FixedClientAccessor(new FixedHealthClient(testCase.Result)));
            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Ensure(result.Status == testCase.Expected,
                $"{testCase.Result.Outcome} should map to {testCase.Expected}");
            Ensure(result.Exception is null,
                $"{testCase.Result.Outcome} should not require exception control flow");
        }
    }

    [Test]
    public async Task RunningClientWithoutReadyConnectionShouldReturnNotReady()
    {
        var accessor = new SharpLinkClientAccessor();
        await using var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTransport(new FailingConnectTransportFactory())
                .DisableRequestTimeout(),
            accessor,
            NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);
        var client = await accessor.GetClientAsync();
        var result = await client.CheckHealthAsync();

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "hosted client must be Running while remote readiness is unavailable");
        Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
            "hosted client must expose NotReady when zero remote connections are ready");
        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.NotReady,
            "Running + zero Ready connections must return the structured NotReady outcome");
        Ensure(result.Status is null, "NotReady must not invent a remote status");

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task UnsupportedRemoteHealthShouldUseStableUnhealthyMapping()
    {
        var check = new SharpLinkRemoteHealthCheck(
            new FixedClientAccessor(new FixedHealthClient(SharpLinkHealthCheckResult.Unsupported)));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Ensure(result.Status == HealthStatus.Unhealthy, "Unsupported must map to Unhealthy");
        Ensure(result.Description == "Remote SharpLink server does not support protocol health checks.",
            "Unsupported mapping must use the documented stable policy");
        Ensure(result.Exception is null, "Unsupported must not attach an expected exception");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FixedClientAccessor(ISharpLinkClient client) : ISharpLinkClientAccessor
    {
        public ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(client);
        }
    }

    private sealed class FixedHealthClient(SharpLinkHealthCheckResult result) : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }

        public T Get<T>() where T : IService
            => throw new NotSupportedException();

        public T GetWithMetadata<T>(SharpLinkMetadata metadata) where T : IService
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => default;

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyReplacementResult
            {
                Succeeded = true,
                ReferencesReleased = true
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingConnectTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("health test connect failure"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
