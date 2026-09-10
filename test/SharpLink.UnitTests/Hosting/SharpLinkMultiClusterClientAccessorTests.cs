using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Hosting;

public sealed class SharpLinkMultiClusterClientAccessorTests
{
    [Test]
    public async Task StopShouldRejectLaterPublicationAndReads()
    {
        var accessor = new SharpLinkMultiClusterClientAccessor();
        accessor.Stop();

        await EnsureThrows<InvalidOperationException>(async () => await accessor.GetClientAsync());
        await EnsureThrows<InvalidOperationException>(() =>
        {
            accessor.SetClient(new FakeMultiClusterClient());
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ConcurrentHostedStopCallersShouldAwaitTheSameCoordinatorCleanup()
    {
        var accessor = new SharpLinkMultiClusterClientAccessor();
        var service = new SharpLinkMultiClusterClientHostedService(
            SharpLinkMultiClusterClientBuilder.Create(),
            accessor,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var client = new BlockingStopMultiClusterClient();
        typeof(SharpLinkMultiClusterClientHostedService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);

        var first = service.StopAsync(CancellationToken.None);
        await client.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.StopAsync(CancellationToken.None);
        var returnedBeforeCleanup = second.IsCompleted;
        client.ReleaseStop();
        await Task.WhenAll(first, second);

        if (returnedBeforeCleanup)
            throw new Exception("Concurrent hosted stop callers must join coordinator cleanup.");
    }

    [Test]
    public async Task DuplicateHostedStartShouldNotDisposeTheExistingClient()
    {
        var accessor = new SharpLinkMultiClusterClientAccessor();
        var service = new SharpLinkMultiClusterClientHostedService(
            SharpLinkMultiClusterClientBuilder.Create(),
            accessor,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var client = new DisposalTrackingMultiClusterClient();
        typeof(SharpLinkMultiClusterClientHostedService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);

        Exception? failure = null;
        try
        {
            await service.StartAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not InvalidOperationException)
            throw new Exception("A second hosted start must be rejected.");
        if (client.DisposeCount != 0)
            throw new Exception("Rejecting a second hosted start must not dispose the existing client.");
        if (!ReferenceEquals(client, typeof(SharpLinkMultiClusterClientHostedService)
                .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)))
        {
            throw new Exception("Rejecting a second hosted start must preserve the existing coordinator owner.");
        }
        if (accessor.GetClientAsync().IsCompleted)
            throw new Exception("Rejecting a second hosted start must not poison the coordinator accessor.");
    }

    private static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private class FakeMultiClusterClient : ISharpLinkMultiClusterClient
    {
        public SharpLinkMultiClusterState State => SharpLinkMultiClusterState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public TContract Get<TContract>() where TContract : IService => throw new NotSupportedException();

        public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster) => SharpLinkConnectionState.Ready;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            SharpLinkClusterKey cluster,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyReplacementResult
            {
                Succeeded = true,
                ReferencesReleased = true
            });

        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposalTrackingMultiClusterClient : FakeMultiClusterClient
    {
        internal int DisposeCount { get; private set; }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingStopMultiClusterClient : ISharpLinkMultiClusterClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkMultiClusterState State => SharpLinkMultiClusterState.Draining;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopStarted.TrySetResult();
            return new ValueTask(_release.Task);
        }

        public TContract Get<TContract>() where TContract : IService => throw new NotSupportedException();
        public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster) => SharpLinkConnectionState.Stopped;
        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            SharpLinkClusterKey cluster,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));
        public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly)
            => default;
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => StopAsync();

        internal void ReleaseStop() => _release.TrySetResult();
    }
}
