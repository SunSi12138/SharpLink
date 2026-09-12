using System.Net;
using System.Reflection;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerDesiredSessionReviewTests
{
    [Test]
    public async Task FutureOnlyShouldNotCreateHandshakeCatchUpIntent()
    {
        await using var server = CreateServer();
        var pinned = server.DesiredSession;
        var configuration = NextConfiguration(pinned);

        var futureOnly = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
            configuration,
            SharpLinkSessionRolloutMode.FutureOnly);

        Ensure(futureOnly.Succeeded && futureOnly.Snapshot.HasValue,
            "FutureOnly publication should succeed");
        var published = futureOnly.Snapshot ?? throw new InvalidOperationException(
            "successful FutureOnly publication must return its desired snapshot");
        Ensure(!server.TryCreateRollingSessionRefreshRequestForTesting(pinned, out _),
            "a session pinned before a FutureOnly publication must not receive handshake catch-up refresh intent");

        var rolling = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
            configuration,
            SharpLinkSessionRolloutMode.RollingRefresh);
        Ensure(rolling.Succeeded,
            "same-config RollingRefresh should be accepted without advancing desired generation");
        Ensure(server.TryCreateRollingSessionRefreshRequestForTesting(pinned, out var request) &&
               request.DesiredGeneration == published.Generation,
            "explicit RollingRefresh should create catch-up intent for the already-published generation");
    }

    [Test]
    public async Task SameGenerationRollingRefreshShouldRescanAfterPreviousScanCompletes()
    {
        await using var server = CreateServer();
        var initial = server.DesiredSession;
        var configuration = NextConfiguration(initial);
        await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
            configuration,
            SharpLinkSessionRolloutMode.FutureOnly);

        var scans = 0;
        server._desiredSessionRolloutTestHook = (_, _) =>
        {
            Interlocked.Increment(ref scans);
            return ValueTask.CompletedTask;
        };
        try
        {
            var first = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh);
            var second = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh);

            Ensure(first.Succeeded && second.Succeeded && scans == 2,
                "each completed same-generation RollingRefresh request should be able to start a fresh stale-session scan");
        }
        finally
        {
            server._desiredSessionRolloutTestHook = null;
        }
    }

    [Test]
    public async Task CallerCancellationShouldNotCancelServerOwnedRollingRefresh()
    {
        await using var server = CreateServer();
        var configuration = NextConfiguration(server.DesiredSession);
        await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
            configuration,
            SharpLinkSessionRolloutMode.FutureOnly);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        server._desiredSessionRolloutTestHook = async (_, token) =>
        {
            Interlocked.Increment(ref scans);
            entered.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        };

        try
        {
            using var callerCancellation = new CancellationTokenSource();
            var cancelledWait = ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh,
                callerCancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            callerCancellation.Cancel();
            await EnsureThrows<OperationCanceledException>(async () => await cancelledWait.ConfigureAwait(false));

            release.TrySetResult();
            var join = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh);
            var retry = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(
                configuration,
                SharpLinkSessionRolloutMode.RollingRefresh);

            Ensure(join.Succeeded && retry.Succeeded && scans >= 2,
                "caller cancellation must leave the server-owned rollout able to finish and a later same-generation retry able to rescan");
        }
        finally
        {
            release.TrySetResult();
            server._desiredSessionRolloutTestHook = null;
        }
    }

    [Test]
    public async Task StoppedServerShouldReturnStructuredLifecycleClosed()
    {
        await using var server = CreateServer();
        var configuration = NextConfiguration(server.DesiredSession);
        await server.StopAsync(TimeSpan.Zero);

        var result = await ((ISharpLinkServer)server).TryPublishDesiredSessionAsync(configuration);

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            "stopped desired-session publication should be an expected structured lifecycle rejection");
    }

    [Test]
    public async Task CustomServerShouldReturnStructuredUnsupportedAndKeepInvalidInputExceptional()
    {
        ISharpLinkServer server = new CustomServer();
        var valid = new SharpLinkServerDesiredSessionConfiguration
        {
            MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes
        };

        var result = await server.TryPublishDesiredSessionAsync(valid);
        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom ISharpLinkServer should receive explicit unsupported result");

        await EnsureThrows<ArgumentOutOfRangeException>(async () =>
            await server.TryPublishDesiredSessionAsync(new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = 1
            }));
    }

    private static SharpLinkServerDesiredSessionConfiguration NextConfiguration(
        SharpLinkServerDesiredSessionSnapshot current)
        => new()
        {
            MaxFramePayloadBytes = Math.Max(
                SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                current.Configuration.MaxFramePayloadBytes / 2)
        };

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(new NoopListener())
            .Build();

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task EnsureThrows<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CustomServer : ISharpLinkServer
    {
        public SharpLinkServerLifecycleState LifecycleState => SharpLinkServerLifecycleState.Created;
        public SharpLinkHealthStatus HealthStatus => SharpLinkHealthStatus.Unhealthy;
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly) => throw new NotSupportedException();
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly, Assembly newAssembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
