using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpLink.Hosting;
using SharpLink.Server;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkServerHostedServiceTests
{
    [Test]
    public async Task StopAsyncShouldCancelRunLoopDisposeServerAndBeIdempotent()
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
            "normal hosted stop must not be reported as a run failure");
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
    public async Task AsynchronousRunFailureShouldStopTheHost()
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
                "the transient StartAsync token must not own the long-lived Run loop");
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
        var runTask = server.RunAsync().AsTask();

        var stopFailure = await CaptureFailureAsync(
            server.StopAsync(TimeSpan.Zero).AsTask());
        var runFailure = await CaptureFailureAsync(runTask);

        Ensure(stopFailure is IOException { Message: "listener cleanup failed" },
            "StopAsync must surface the owned listener cleanup failure");
        Ensure(runFailure is IOException { Message: "listener cleanup failed" },
            "the shared Run operation must observe the same failed stop");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "a cleanup failure must leave the server unhealthy");
    }

    [Test]
    public async Task HostedStopShouldPreserveCancellationAndListenerCleanupFailure()
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

        var stopTask = hosted.StopAsync(cancelled.Token);
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.ReleaseDispose();
        var failure = await CaptureFailureAsync(stopTask);

        var failures = failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : failure is null ? [] : [failure];
        Ensure(failures.Any(static exception => exception is OperationCanceledException),
            "Hosted Stop must preserve caller cancellation");
        Ensure(failures.Any(static exception => exception is IOException { Message: "listener cleanup failed" }),
            "Hosted Stop must preserve later listener cleanup failure");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldReturnFaultedWhenFrameworkCleanupExceedsBudget()
    {
        var transport = new DelayedDisposeTransport();
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(transport)
            .Build();
        var runTask = server.RunAsync().AsTask();

        var started = Stopwatch.GetTimestamp();
        await server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(7));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Ensure(elapsed >= TimeSpan.FromSeconds(4), "cleanup budget must be allowed before faulting");
        Ensure(elapsed < TimeSpan.FromSeconds(7), "server stop must be bounded by the cleanup budget");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "framework cleanup timeout must leave the server unhealthy");

        transport.ReleaseDispose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
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
