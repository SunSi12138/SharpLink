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
        using var workersCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accessor = new SharpLinkClientAccessor();
        var client = new FakeSharpLinkClient();
        Exception? publicationFailure = null;

        var publish = LongRunningTestWorker.Run(() =>
        {
            try
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    start.SignalAndWait(workersCancellation.Token);
                    try
                    {
                        accessor.SetClient(client);
                    }
                    catch (InvalidOperationException exception)
                    {
                        publicationFailure = exception;
                    }
                    start.SignalAndWait(workersCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (workersCancellation.IsCancellationRequested)
            {
            }
            catch
            {
                workersCancellation.Cancel();
                throw;
            }
        });
        var stop = LongRunningTestWorker.Run(() =>
        {
            try
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    start.SignalAndWait(workersCancellation.Token);
                    accessor.Stop();
                    start.SignalAndWait(workersCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (workersCancellation.IsCancellationRequested)
            {
            }
            catch
            {
                workersCancellation.Cancel();
                throw;
            }
        });
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                accessor = new SharpLinkClientAccessor();
                publicationFailure = null;
                start.SignalAndWait(workersCancellation.Token);
                start.SignalAndWait(workersCancellation.Token);

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
        finally
        {
            workersCancellation.Cancel();
            await LongRunningTestWorker.JoinAsync(publish, TimeSpan.FromSeconds(10));
            await LongRunningTestWorker.JoinAsync(stop, TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task HostedStartShouldPublishRunningClientWhenRemoteConnectionFails()
    {
        var accessor = new SharpLinkClientAccessor();
        await using var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTransport(new ThrowingLifecycleTransportFactory())
                .DisableRequestTimeout(),
            accessor,
            NullLoggerFactory.Instance);

        await service.StartAsync(CancellationToken.None);
        var client = await accessor.GetClientAsync();

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "hosted start must publish a running local client even when the remote endpoint is unavailable");
        Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
            "remote connection failure must be represented as NotReady rather than Host startup failure");

        await service.StopAsync(CancellationToken.None);
        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
            "hosted StopAsync must stop the locally running client");
    }

    [Test]
    [TUnit.Core.Timeout(60_000)]
    public async Task HostedStartShouldPublishRuntimeBeforeStaticReadinessTargetConverges(
        CancellationToken cancellationToken)
    {
        var first = new GatedConnectTransportFactory();
        var second = new GatedConnectTransportFactory();
        var accessor = new SharpLinkClientAccessor();
        await using var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseEndpoints(
                [
                    new SharpLinkEndpoint
                    {
                        Id = "first",
                        Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
                    },
                    new SharpLinkEndpoint
                    {
                        Id = "second",
                        Address = new SharpLinkTcpAddress("127.0.0.1", 5002)
                    }
                ],
                endpoint => endpoint.Id == "first" ? first : second)
                .UseCluster(options =>
                {
                    options.MinReadyEndpoints = 2;
                    options.MaxConnections = 2;
                    options.MaxConnectionsPerEndpoint = 1;
                }),
            accessor,
            NullLoggerFactory.Instance);

        var accessorWait = accessor.GetClientAsync().AsTask();
        await service.StartAsync(cancellationToken);
        var client = await accessorWait.WaitAsync(cancellationToken);
        var startupSnapshot = client.GetReadinessSnapshot();

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
            "HostedService must publish after local runtime startup without waiting for connectivity");
        Ensure(client.Readiness == SharpLinkReadinessState.NotReady && startupSnapshot.ReadyConnections == 0,
            "published runtime must remain NotReady before either gated endpoint connects");

        await Task.WhenAll(first.ConnectStarted.Task, second.ConnectStarted.Task).WaitAsync(cancellationToken);
        first.ReleaseConnect();
        await first.ConnectCompleted.Task.WaitAsync(cancellationToken);
        var snapshot = await client.WaitForReadinessAsync(1, cancellationToken);

        Ensure(first.ConnectCompleted.Task.IsCompleted && !second.ConnectCompleted.Task.IsCompleted,
            "one endpoint may become usable without releasing the second endpoint gate");
        Ensure(snapshot.State == SharpLinkConnectionState.Ready &&
               snapshot.ActiveEndpoints == 2 &&
               snapshot.ReadyEndpoints == 1 &&
               snapshot.ReadyConnections == 1 &&
               snapshot.TargetReadyEndpoints == 2,
            "the published client must distinguish connectivity from the unconverged static target");
        Ensure(!snapshot.MeetsTarget,
            "one ready endpoint must not satisfy a configured two-endpoint readiness target");
    }

    [Test]
    public async Task DuplicateHostedStartShouldNotDisposeTheExistingClient()
    {
        var accessor = new SharpLinkClientAccessor();
        var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty),
            accessor,
            NullLoggerFactory.Instance);
        var client = new DisposalTrackingClient();
        typeof(SharpLinkClientHostedService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);

        var failure = await CaptureExceptionAsync(service.StartAsync(CancellationToken.None));

        Ensure(failure is InvalidOperationException,
            "a second hosted start must be rejected");
        Ensure(client.DisposeCount == 0,
            "rejecting a second hosted start must not dispose the existing client");
        Ensure(ReferenceEquals(client, typeof(SharpLinkClientHostedService)
                .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)),
            "rejecting a second hosted start must preserve the existing client owner");
        Ensure(!accessor.GetClientAsync().IsCompleted,
            "rejecting a second hosted start must not poison the client accessor");
    }

    [Test]
    public async Task ConcurrentHostedStopCallersShouldAwaitTheSameClientCleanup()
    {
        var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new ThrowingLifecycleTransportFactory()),
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

    [Test]
    public async Task CancelledHostedStopShouldStillDisposeTransferredClient()
    {
        var service = new SharpLinkClientHostedService(
            SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new ThrowingLifecycleTransportFactory()),
            new SharpLinkClientAccessor(),
            NullLoggerFactory.Instance);
        var client = new CancellationSensitiveClient();
        typeof(SharpLinkClientHostedService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stopFailure = await CaptureExceptionAsync(service.StopAsync(cancellation.Token));
        _ = await CaptureExceptionAsync(service.DisposeAsync().AsTask());

        Ensure(stopFailure is OperationCanceledException,
            "hosted Stop must preserve caller cancellation");
        Ensure(client.DisposeCount == 1,
            "hosted Stop cancellation must not lose the transferred Client owner");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
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

    private class FakeSharpLinkClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
    }

    private sealed class DisposalTrackingClient : FakeSharpLinkClient
    {
        internal int DisposeCount { get; private set; }

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLifecycleTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(
                new InvalidOperationException("hosted connect failed"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedConnectTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var connection = await _inner.ConnectAsync(cancellationToken);
            ConnectCompleted.TrySetResult();
            return connection;
        }

        internal void ReleaseConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
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



        public T GetWithMetadata<T>(SharpLinkMetadata metadata) where T : IService


            => throw new NotSupportedException();
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

    private sealed class CancellationSensitiveClient : ISharpLinkClient
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public SharpLinkConnectionState State => SharpLinkConnectionState.Draining;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));
        public T Get<T>() where T : IService => throw new NotSupportedException();


        public T GetWithMetadata<T>(SharpLinkMetadata metadata) where T : IService

            => throw new NotSupportedException();
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
    }
}
