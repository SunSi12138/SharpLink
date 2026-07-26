using System.Threading;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkClientAccessorTests
{
    [Test]
    public async Task GetClientAsyncShouldWaitUntilClientIsPublished()
    {
        var accessor = new SharpLinkClientAccessor();
        var wait = accessor.GetClientAsync();

        Ensure(!wait.IsCompleted, "client wait should remain pending before publication");

        var client = new FakeSharpLinkClient();
        accessor.SetClient(client);

        var resolved = await wait;
        Ensure(ReferenceEquals(client, resolved), "wait should resolve the published client instance");
    }

    [Test]
    public async Task GetClientAsyncShouldCompleteSynchronouslyWhenClientAlreadyExists()
    {
        var accessor = new SharpLinkClientAccessor();
        var client = new FakeSharpLinkClient();
        accessor.SetClient(client);

        var wait = accessor.GetClientAsync();

        Ensure(wait.IsCompletedSuccessfully, "client wait should complete synchronously after publication");
        Ensure(ReferenceEquals(client, await wait), "resolved client should be the published instance");
    }

    [Test]
    public async Task GetClientAsyncShouldFailAfterStopWhenClientWasNeverPublished()
    {
        var accessor = new SharpLinkClientAccessor();
        accessor.Stop();

        try
        {
            await accessor.GetClientAsync();
            throw new Exception("expected GetClientAsync to throw after stop");
        }
        catch (InvalidOperationException ex)
        {
            Ensure(ex.Message.Contains("host has already stopped", StringComparison.Ordinal), "exception message should describe the stopped host");
        }
    }

    [Test]
    public async Task HostedStartShouldPreserveConnectAndCleanupFailures()
    {
        var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create().UseTransport(new ThrowingLifecycleTransportFactory()),
            new SharpLinkClientAccessor(),
            NullLoggerFactory.Instance);

        Exception failure;
        try
        {
            await service.StartAsync(CancellationToken.None);
            throw new Exception("expected hosted client start failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsMessage(failure, "hosted connect failed"),
            "hosted start must retain its primary connect failure");
        Ensure(ContainsMessage(failure, "hosted cleanup failed"),
            "hosted start must retain its cleanup failure");
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeSharpLinkClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public T Get<T>() where T : IService
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
    }

    private sealed class ThrowingLifecycleTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("hosted connect failed"));

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException("hosted cleanup failed"));
    }
}
