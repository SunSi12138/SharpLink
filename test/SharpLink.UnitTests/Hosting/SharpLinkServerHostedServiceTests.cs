using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Linq;
using System.Threading;
using System.Reflection;
using SharpLink.Hosting;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkServerHostedServiceTests
{
    [Test]
    public async Task StopAsyncShouldCancelRunLoopDisposeServerAndBeIdempotent()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
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
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(transport);
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
    public async Task ExpectedRunFailureDuringHostedStopShouldNotStopTheHost()
    {
        var lifetime = new TestHostApplicationLifetime();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var hosted = new SharpLinkServerHostedService(
            SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new FailingDisposeTransport()),
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
            "an expected Run fault after hosted Stop begins must not stop the owning Host");
    }

    [Test]
    public async Task CompletedHostedStopShouldRejectLaterStart()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var duplicateHosted = new SharpLinkServerHostedService(
            SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new BlockingTransport()),
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
            SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new BlockingTransport()),
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
    public async Task UnexpectedSuccessfulRunCompletionShouldStopTheHost()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(transport);
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
        await lifetime.StopRequested.Task;

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task SuccessfulStartupShouldNotRetainItsCancellationToken()
    {
        var transport = new BlockingTransport();
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(transport);
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
        var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
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
        var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(transport);
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
    public async Task ServerStopShouldReturnFaultedWhenFrameworkCleanupExceedsBudget()
    {
        var provider = new ManualTimeProvider();
        var transport = new DelayedDisposeTransport();
        var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTimeProvider(provider)
            .UseTransport(transport)
            .Build();
        var runTask = server.RunAsync().AsTask();

        var stop = server.StopAsync(TimeSpan.Zero).AsTask();
        Ensure(transport.DisposeStarted.Task.IsCompleted,
            "framework cleanup must start before its provider-owned budget is armed");
        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!stop.IsCompleted,
            "framework cleanup must remain pending one provider tick before its budget");
        Ensure(((SharpLinkServer)server).DeferredTaskSnapshotForDiagnostics.ShutdownCleanupObserver is null,
            "the deferred cleanup observer must not be published before the framework budget expires");

        provider.Advance(TimeSpan.FromTicks(1));
        await stop;
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "framework cleanup timeout must leave the server unhealthy");
        var deferred = ((SharpLinkServer)server).DeferredTaskSnapshotForDiagnostics;
        Ensure(deferred.ShutdownCleanupObserver is not null and not TaskStatus.RanToCompletion,
            "timed-out framework cleanup must remain continuously observed and diagnosable");
        var shutdownCleanupObserver = (Task)(typeof(SharpLinkServer).GetField(
            "_shutdownCleanupObserver",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(server) ?? throw new Exception("cannot find Server shutdown cleanup observer owner"));

        transport.ReleaseDispose();
        await shutdownCleanupObserver;
        Ensure(((SharpLinkServer)server).DeferredTaskSnapshotForDiagnostics.ShutdownCleanupObserver ==
               TaskStatus.RanToCompletion,
            "framework cleanup observer must complete after the listener owner releases");
        await runTask;
        Ensure(provider.ActiveTimerCount == 0,
            "framework cleanup completion must leave no provider timer behind");
    }

    [Test]
    public async Task ServerGracefulActiveCallShouldForceAtProviderEqualityAndObserveDeferredCleanup()
    {
        var provider = new ManualTimeProvider();
        var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTimeProvider(provider)
            .UseTransport(new BlockingTransport())
            .Build();
        var concrete = (SharpLinkServer)server;
        var runTask = server.RunAsync().AsTask();
        var activeCalls = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server active-call counter");
        var callsDrained = (TaskCompletionSource<bool>)(typeof(SharpLinkServer).GetField(
            "_callsDrained",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(server) ?? throw new Exception("cannot find Server call-drain signal"));
        activeCalls.SetValue(server, 1);

        var stop = server.StopAsync(TimeSpan.FromSeconds(5)).AsTask();
        provider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!stop.IsCompleted && concrete.LastStopDiagnostics is null,
            "an active call must remain graceful one owner-provider tick before its deadline");

        provider.Advance(TimeSpan.FromTicks(1));
        await stop;
        Ensure(concrete.LastStopDiagnostics is { GlobalActiveCalls: 1 },
            "the equality winner must capture the one call forced beyond grace");
        Ensure(concrete.DeferredTaskSnapshotForDiagnostics.DeferredServiceCleanup is not null and
               not TaskStatus.RanToCompletion,
            "forced active-call cleanup must remain continuously observed until the call owner releases");
        var deferredCleanup = (Task)(typeof(SharpLinkServer).GetField(
            "_deferredServiceCleanupTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(server) ?? throw new Exception("cannot find Server deferred service cleanup owner"));

        activeCalls.SetValue(server, 0);
        callsDrained.TrySetResult(true);
        await deferredCleanup;
        Ensure(concrete.DeferredTaskSnapshotForDiagnostics.DeferredServiceCleanup ==
               TaskStatus.RanToCompletion,
            "deferred service cleanup must complete after the active-call owner releases");
        await runTask;
        Ensure(provider.ActiveTimerCount == 0,
            "graceful force and deferred cleanup completion must leave no provider timer");
    }

    [Test]
    public async Task TimerRangeExceedingServerGracefulWaitShouldRemainPending()
    {
        var method = typeof(SharpLinkServer).GetMethod(
            "WaitUntilWithProviderAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Task), typeof(long), typeof(TimeProvider)],
            modifiers: null)
            ?? throw new Exception("cannot find Server graceful wait helper");
        var provider = new ManualTimeProvider();
        var owner = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = (Task<bool>)method.Invoke(
            null,
            [owner.Task, long.MaxValue, provider])!;

        Ensure(provider.ActiveTimerCount == 1,
            "a timer-range-exceeding graceful wait must own one provider timer");
        provider.Advance(TimeSpan.FromMilliseconds(int.MaxValue));
        Ensure(!wait.IsCompleted,
            "reaching the first maximum timer slice must not exhaust a long graceful deadline");
        owner.TrySetResult(true);
        var completed = await wait;

        Ensure(completed,
            "owner completion must finish the long graceful wait successfully");
        await provider.WaitForTimersDrainedAsync();
        Ensure(provider.ActiveTimerCount == 0,
            "owner completion must dispose the provider timer without a real-time wait");
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
