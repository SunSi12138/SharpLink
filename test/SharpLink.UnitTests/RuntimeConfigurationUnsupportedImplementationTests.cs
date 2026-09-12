using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests;

public sealed class RuntimeConfigurationUnsupportedImplementationTests
{
    [Test]
    public void CustomClientShouldReceiveStructuredUnsupportedResult()
    {
        ISharpLinkClient client = new CustomClient();

        var result = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(1));

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom client should receive explicit non-throwing unsupported result");
    }

    [Test]
    public void CustomServerShouldReceiveStructuredUnsupportedResult()
    {
        ISharpLinkServer server = new CustomServer();

        var result = server.TryDisableAdmissionControl();

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom server should receive explicit non-throwing unsupported result");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class CustomClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Created;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkHealthCheckResult>(new NotSupportedException());

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CustomServer : ISharpLinkServer
    {
        public SharpLinkServerLifecycleState LifecycleState => SharpLinkServerLifecycleState.Created;

        public SharpLinkHealthStatus HealthStatus => SharpLinkHealthStatus.Unhealthy;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask StopAsync(
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
