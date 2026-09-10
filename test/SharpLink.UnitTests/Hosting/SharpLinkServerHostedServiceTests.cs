using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Reflection;
using SharpLink.Hosting;
using SharpLink.Server;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkServerHostedServiceTests
{
    [Test]
    public async Task StopAsyncShouldStopServerDisposeTransportAndBeIdempotent()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create()
            .UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var readiness = new SharpLinkServerReadiness();
        var lifetime = new TestHostApplicationLifetime();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            readiness,
            lifetime);

        await hosted.StartAsync(CancellationToken.None);
        Ensure(readiness.Status == SharpLinkHealthStatus.Ready,
            "readiness should be ready after hosted service starts");
        await hosted.StopAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Ensure(transport.DisposeCalled, "transport should be disposed when hosted service stops");
        Ensure(readiness.Status == SharpLinkHealthStatus.Unhealthy,
            "readiness should be unhealthy after hosted service stops");
        Ensure(!lifetime.ApplicationStopping.IsCancellationRequested,
            "normal hosted stop must not be reported as a terminal failure");
    }

    [Test]
    public async Task ConcurrentStopCallersShouldAwaitTheSameServerCleanup()
    {
        var transport = new DelayedDisposeTransport();
        var builder = SharpLinkServerBuilder.Create().UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            new SharpLinkServerReadiness(),
            new TestHostApplicationLifetime());

        await hosted.StartAsync(CancellationToken.None);
        var first = hosted.StopAsync(CancellationToken.None);
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = hosted.StopAsync(CancellationToken.None);

        Ensure(!second.IsCompleted, "concurrent StopAsync must await the active server cleanup");
        transport.ReleaseDispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AsynchronousServerFailureShouldStopTheHost()
    {
        var transport = new DeferredFailureTransport();
        var lifetime = new TestHostApplicationLifetime();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSharpLinkServer(builder => builder.UseTransport(transport));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>()
            .Single(service => service is SharpLinkServerHostedService);

        await hosted.StartAsync(CancellationToken.None);
        transport.Fail(new IOException("deferred accept failed"));

        try
        {
            await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            try
            {
                await hosted.StopAsync(CancellationToken.None);
            }
            catch (IOException exception) when (exception.Message == "deferred accept failed")
            {
            }
        }
    }

    [Test]
    public async Task ExpectedServerFailureDuringHostedStopShouldNotStopTheHost()
    {
        var lifetime = new TestHostApplicationLifetime();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var hosted = new SharpLinkServerHostedService(
            SharpLinkServerBuilder.Create().UseTransport(new FailingDisposeTransport()),
            NullLoggerFactory.Instance,
            provider,
            new SharpLinkServerReadiness(),
            lifetime);
        await hosted.StartAsync(CancellationToken.None);

        var stopFailure = await CaptureFailureAsync(hosted.StopAsync(CancellationToken.None));
        await Task.Delay(100);

        Ensure(stopFailure is IOException { Message: "listener cleanup failed" },
            "Hosted Stop must preserve the expected listener cleanup failure");
        Ensure(!lifetime.ApplicationStopping.IsCancellationRequested,
            "an expected Server fault after hosted Stop begins must not stop the owning Host");
    }

    [Test]
    public async Task CompletedHostedStopShouldRejectLaterStart()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var duplicateHosted = new SharpLinkServerHostedService(
            SharpLinkServerBuilder.Create().UseTransport(new BlockingTransport()),
            NullLoggerFactory.Instance,
            provider,
            new SharpLinkServerReadiness(),
            new TestHostApplicationLifetime());
        await duplicateHosted.StartAsync(CancellationToken.None);
        var firstServer = (ISharpLinkServer)typeof(SharpLinkServerHostedService)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(duplicateHosted)!;
        var duplicateFailure = await CaptureFailureAsync(
            duplicateHosted.StartAsync(CancellationToken.None));
        var currentServer = (ISharpLinkServer)typeof(SharpLinkServerHostedService)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(duplicateHosted)!;
        await duplicateHosted.StopAsync(CancellationToken.None);
        if (!ReferenceEquals(firstServer, currentServer))
            await firstServer.DisposeAsync();

        var readiness = new SharpLinkServerReadiness();
        var hosted = new SharpLinkServerHostedService(
            SharpLinkServerBuilder.Create().UseTransport(new BlockingTransport()),
            NullLoggerFactory.Instance,
            provider,
            readiness,
            new TestHostApplicationLifetime());
        await hosted.StopAsync(CancellationToken.None);

        var startFailure = await CaptureFailureAsync(hosted.StartAsync(CancellationToken.None));
        var server = (ISharpLinkServer?)typeof(SharpLinkServerHostedService)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(hosted);
        if (server is not null)
            await server.DisposeAsync();

        Ensure(duplicateFailure is InvalidOperationException
            { Message: "The SharpLink server host has already started." },
            "a duplicate hosted Start must be rejected before replacing the owned server");
        Ensure(startFailure is InvalidOperationException,
            "a completed hosted Stop must be a terminal barrier to later Start");
        Ensure(readiness.Status == SharpLinkHealthStatus.Unhealthy,
            "a rejected post-stop Start must not publish readiness");
    }

    [Test]
    public async Task UnexpectedServerTerminationShouldStopTheHost()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create().UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var lifetime = new TestHostApplicationLifetime();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            new SharpLinkServerReadiness(),
            lifetime);

        await hosted.StartAsync(CancellationToken.None);
        var server = (ISharpLinkServer)(typeof(SharpLinkServerHostedService)
            .GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(hosted) ?? throw new Exception("hosted server was not published"));
        await server.StopAsync(TimeSpan.Zero);
        var completed = await Task.WhenAny(
            lifetime.StopRequested.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500)));

        await hosted.StopAsync(CancellationToken.None);
        Ensure(ReferenceEquals(completed, lifetime.StopRequested.Task),
            "an unexpected successful Server termination must stop the owning Host");
    }

    [Test]
    public async Task SuccessfulStartupShouldNotRetainItsCancellationToken()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create().UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var readiness = new SharpLinkServerReadiness();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            readiness,
            new TestHostApplicationLifetime());
        using var startupCancellation = new CancellationTokenSource();

        await hosted.StartAsync(startupCancellation.Token);
        startupCancellation.Cancel();
        _ = await Task.WhenAny(
            transport.DisposeObserved.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500)));
        var stoppedByStartupToken = transport.DisposeCalled;
        try
        {
            Ensure(!stoppedByStartupToken,
                "the transient StartAsync token must not own the long-lived Server runtime");
            Ensure(readiness.Status == SharpLinkHealthStatus.Ready,
                "startup-token cancellation after publication must not change readiness");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ServerStopShouldSurfaceImmediateListenerCleanupFailure()
    {
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(new FailingDisposeTransport())
            .Build();
        await server.StartAsync();
        var shutdownWait = server.WaitForShutdownAsync();

        var stopFailure = await CaptureFailureAsync(
            server.StopAsync(TimeSpan.Zero).AsTask());
        var terminalFailure = await CaptureFailureAsync(shutdownWait);

        Ensure(stopFailure is IOException { Message: "listener cleanup failed" },
            "StopAsync must surface the owned listener cleanup failure");
        Ensure(terminalFailure is IOException { Message: "listener cleanup failed" },
            "the terminal shutdown wait must observe the same failed stop");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "a cleanup failure must leave the server unhealthy");
    }

    [Test]
    public async Task HostedStopCallerCancellationShouldNotCancelSharedCleanup()
    {
        var transport = new DelayedFailingDisposeTransport();
        var builder = SharpLinkServerBuilder.Create().UseTransport(transport);
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var hosted = new SharpLinkServerHostedService(
            builder,
            NullLoggerFactory.Instance,
            provider,
            new SharpLinkServerReadiness(),
            new TestHostApplicationLifetime());
        await hosted.StartAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var cancelledWait = hosted.StopAsync(cancelled.Token);
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationFailure = await CaptureFailureAsync(cancelledWait);
        var sharedStop = hosted.StopAsync(CancellationToken.None);

        Ensure(cancellationFailure is OperationCanceledException,
            "Hosted Stop caller cancellation must cancel only that caller's wait");
        Ensure(!sharedStop.IsCompleted,
            "Hosted Stop caller cancellation must not cancel or force the shared cleanup");

        transport.ReleaseDispose();
        var cleanupFailure = await CaptureFailureAsync(sharedStop);
        Ensure(cleanupFailure is IOException { Message: "listener cleanup failed" },
            "a later Hosted Stop caller must join and observe the shared cleanup failure");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldReturnFaultedWhenFrameworkCleanupExceedsBudget()
    {
        var transport = new DelayedDisposeTransport();
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .Build();
        await server.StartAsync();
        var shutdownWait = server.WaitForShutdownAsync();

        var started = Stopwatch.GetTimestamp();
        var stopFailure = await CaptureFailureAsync(
            server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(7)));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Ensure(elapsed >= TimeSpan.FromSeconds(4), "cleanup budget must be allowed before faulting");
        Ensure(elapsed < TimeSpan.FromSeconds(7), "server stop must be bounded by the cleanup budget");
        Ensure(stopFailure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "a bounded framework cleanup timeout must surface the terminal Faulted state");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "framework cleanup timeout must leave the server unhealthy");
        Ensure(shutdownWait.IsCompleted,
            "StopAsync completion must publish terminal completion before returning");
        var terminalFailure = await CaptureFailureAsync(shutdownWait);
        Ensure(terminalFailure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "terminal wait must propagate the same cleanup-timeout failure class");

        transport.ReleaseDispose();
    }

    [Test]
    public async Task TimerRangeExceedingServerGracefulWaitShouldRemainPending()
    {
        var method = typeof(SharpLinkServer).GetMethod(
            "WaitUntilAsync",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server graceful wait helper");
        var owner = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = (Task<bool>)method.Invoke(null, [owner.Task, long.MaxValue])!;

        await Task.Delay(50);
        var completedBeforeOwner = wait.IsCompleted;
        owner.TrySetResult(true);
        var failure = await CaptureFailureAsync(wait);

        Ensure(!completedBeforeOwner,
            "a timer-range-exceeding graceful wait must not fail before its owner completes");
        Ensure(failure is null, $"long graceful wait failed as {failure?.GetType().Name}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
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

    private sealed class BlockingTransport : IServerTransportListener
    {
        private int _disposed;
        internal TaskCompletionSource DisposeObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DisposeCalled => Volatile.Read(ref _disposed) == 1;
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            DisposeObserved.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingDisposeTransport : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new IOException("listener cleanup failed"));
    }

    private sealed class DelayedDisposeTransport : IServerTransportListener
    {
        private readonly TaskCompletionSource<bool> _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            return new(_disposeRelease.Task);
        }

        public void ReleaseDispose() => _disposeRelease.TrySetResult(true);
    }

    private sealed class DelayedFailingDisposeTransport : IServerTransportListener
    {
        private readonly TaskCompletionSource<bool> _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await _disposeRelease.Task;
            throw new IOException("listener cleanup failed");
        }

        public void ReleaseDispose() => _disposeRelease.TrySetResult(true);
    }

    private sealed class DeferredFailureTransport : IServerTransportListener
    {
        private readonly TaskCompletionSource<ITransportConnection> _accept =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => new(_accept.Task.WaitAsync(cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void Fail(Exception exception) => _accept.TrySetException(exception);
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        internal TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopRequested.TrySetResult();
            _stopping.Cancel();
        }
    }
}
