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
    public async Task ConcurrentPublicationMustNotResurrectClientAfterStop()
    {
        const int attempts = 100_000;
        using var start = new Barrier(3);
        var accessor = new SharpLinkClientAccessor();
        var client = new FakeSharpLinkClient();
        Exception? publicationFailure = null;

        var publish = Task.Run(() =>
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                start.SignalAndWait();
                try
                {
                    accessor.SetClient(client);
                }
                catch (InvalidOperationException exception)
                {
                    publicationFailure = exception;
                }
                start.SignalAndWait();
            }
        });
        var stop = Task.Run(() =>
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                start.SignalAndWait();
                accessor.Stop();
                start.SignalAndWait();
            }
        });

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            accessor = new SharpLinkClientAccessor();
            publicationFailure = null;
            start.SignalAndWait();
            start.SignalAndWait();

            try
            {
                await accessor.GetClientAsync();
                throw new Exception($"attempt {attempt} returned a client after stop");
            }
            catch (InvalidOperationException)
            {
            }

            Ensure(publicationFailure is null ||
                publicationFailure.Message.Contains("host has already stopped", StringComparison.Ordinal),
                "publication may only fail because stop won the race");
        }

        await Task.WhenAll(publish, stop);
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

    [Test]
    public async Task ConcurrentHostedStopCallersShouldAwaitTheSameClientCleanup()
    {
        var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create().UseTransport(new ThrowingLifecycleTransportFactory()),
            new SharpLinkClientAccessor(),
            NullLoggerFactory.Instance);
        var client = new BlockingStopClient();
        typeof(SharpLinkClientHostedService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);

        var first = service.StopAsync(CancellationToken.None);
        await client.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.StopAsync(CancellationToken.None);
        var returnedBeforeCleanup = second.IsCompleted;
        client.ReleaseStop();
        await Task.WhenAll(first, second);

        Ensure(!returnedBeforeCleanup,
            "concurrent hosted stop callers must join the same client cleanup");
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

    private sealed class BlockingStopClient : ISharpLinkClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => SharpLinkConnectionState.Draining;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted.TrySetResult();
            return new ValueTask(_release.Task);
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));

        public T Get<T>() where T : IService => throw new NotSupportedException();
        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly) => default;
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => StopAsync();

        internal void ReleaseStop() => _release.TrySetResult();
    }
}
