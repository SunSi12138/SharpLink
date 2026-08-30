using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleStartStopSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLifecycleStartStopTests
{
    [Test]
    public async Task ConcurrentConnectsShouldShareOneAttemptAndReadyLoopSet()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);

        var connects = new Task[16];
        for (var index = 0; index < connects.Length; index++)
            connects[index] = client.ConnectAsync().AsTask();
        await Task.WhenAll(connects);

        Ensure(transport.ConnectCount == 1, "concurrent calls should share one transport attempt");
        Ensure(client.State == SharpLinkConnectionState.Ready, "client state should be ready");
        await client.ConnectAsync();
        Ensure(transport.ConnectCount == 1, "repeated ready connect should complete without new loops");
    }

    [Test]
    public async Task SharedFixedConnectShouldSurviveFirstWaiterCancellation()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        using var cancellation = new CancellationTokenSource();

        var cancelledWaiter = client.ConnectAsync(cancellation.Token).AsTask();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var survivingWaiter = client.ConnectAsync().AsTask();
        cancellation.Cancel();

        await EnsureCancelledAsync(cancelledWaiter);
        Ensure(!survivingWaiter.IsCompleted,
            "one caller cancelling its wait must not cancel the shared client-owned connect attempt");

        transport.ReleaseConnect();
        await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "the shared fixed connect should still publish a ready connection");
    }

    [Test]
    public async Task EndpointClusterHandshakeTimeoutsShouldRetainStructuredCause()
    {
        var staticFactories = new List<HangingHandshakeTransportFactory>();
        await using (var staticClient = CreateClientBuilder()
            .UseEndpoints(
                [CreateEndpoint("first", 5001), CreateEndpoint("second", 5002)],
                _ =>
                {
                    var factory = new HangingHandshakeTransportFactory();
                    staticFactories.Add(factory);
                    return factory;
                })
            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(20))
            .Build())
        {
            var exception = await CaptureSharpLinkExceptionAsync(staticClient.ConnectAsync().AsTask());
            Ensure(ContainsHandshakeTimeout(exception),
                "static endpoint clusters must preserve the structured handshake-timeout cause");
        }

        var dynamicFactory = new HangingHandshakeTransportFactory();
        await using var dynamicClient = CreateClientBuilder()
            .UseEndpointResolver(
                new FixedSnapshotResolver(new SharpLinkEndpointSnapshot(1, [CreateEndpoint("dynamic", 5003)])),
                _ => dynamicFactory)
            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(20))
            .Build();
        var dynamicException = await CaptureSharpLinkExceptionAsync(dynamicClient.ConnectAsync().AsTask());
        Ensure(ContainsHandshakeTimeout(dynamicException),
            "dynamic endpoint clusters must preserve the structured handshake-timeout cause");
    }

    [Test]
    public async Task StopAsyncShouldNotRunShutdownCallbacksBeforeReturning()
    {
        var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var shutdownField = typeof(SharpLinkClient).GetField(
            "_shutdownCts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find client shutdown source");
        var shutdown = (CancellationTokenSource)shutdownField.GetValue(client)!;
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = shutdown.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });
        var stopReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = LongRunningTestWorker.Run(() =>
        {
            var stop = client.StopAsync().AsTask();
            stopReturned.TrySetResult(stop);
        });
        try
        {
            await callbackStarted.Task;
            Ensure(stopReturned.Task.IsCompleted,
                "an async StopAsync call must return before a blocking cancellation callback finishes");

            releaseCallback.Set();
            await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            await (await stopReturned.Task).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCallback.Set();
            await LongRunningTestWorker.JoinAsync(invocation, TimeSpan.FromSeconds(2));
            if (stopReturned.Task.IsCompletedSuccessfully)
                await LongRunningTestWorker.JoinAsync(await stopReturned.Task, TimeSpan.FromSeconds(2));
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task FailedConnectShouldPreservePrimaryAndCleanupFailures()
    {
        await using var client = ClientBuilderTestHelper.Build(new CleanupFailingHandshakeTransportFactory());

        Exception failure;
        try
        {
            await client.ConnectAsync();
            throw new Exception("expected connect failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsException(failure, static exception =>
                exception is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed }),
            "connect failure must retain the primary handshake/connection error");
        Ensure(ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "transport cleanup failed" }),
            "connect failure must retain the cleanup error");
    }

    [Test]
    public async Task InitialConnectFailureShouldRemainExternallyObservedAndNotFailStopTwice()
    {
        var client = ClientBuilderTestHelper.Build(new NonConnectingFactory());

        Exception connectFailure;
        try
        {
            await client.ConnectAsync();
            throw new Exception("expected initial connect failure");
        }
        catch (Exception exception)
        {
            connectFailure = exception;
        }
        Ensure(connectFailure is NotSupportedException,
            "the initial connect caller must observe the transport failure");

        await client.StopAsync();

        var snapshot = client.FrameworkTaskSnapshotForDiagnostics;
        Ensure(snapshot.IsSealed && snapshot.IsDrained,
            "stop must seal and drain initial-connect supervision");
        Ensure(snapshot.TotalTracked == 1 && snapshot.ActiveTasks == 0,
            "the initial connect task must be supervised exactly once and fully drained");
        Ensure(snapshot.ExternallyObservedTasks == 0 && snapshot.RetainedFailures == 0,
            "an externally observed initial-connect failure must not be retained for duplicate stop reporting");
    }

    [Test]
    public async Task InitialPoolRollbackShouldPreserveConnectAndCleanupFailures()
    {
        var client = ClientBuilderTestHelper.Build(
            new InitialPoolRollbackFailingTransportFactory(),
            builder => builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            }));

        Exception failure;
        try
        {
            await client.ConnectAsync();
            throw new Exception("expected initial pool connection failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "second connection failed" }),
            "initial pool rollback must retain the connection failure");
        Ensure(ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "ready connection cleanup failed" }),
            "initial pool rollback must retain the ready connection cleanup failure");
        Ensure(client.State == SharpLinkConnectionState.Faulted,
            "cleanup failure must not strand the client in Connecting state");

        try
        {
            await client.StopAsync();
        }
        catch
        {
        }
    }

    [Test]
    public async Task StopShouldBeIdempotentAndRejectLaterConnects()
    {
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        await Task.WhenAll(
            client.StopAsync().AsTask(),
            client.StopAsync().AsTask());
        Ensure(client.State == SharpLinkConnectionState.Stopped, "stopped state");

        try
        {
            await client.ConnectAsync();
            throw new Exception("expected connect after stop to fail");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed, "connect-after-stop error code");
        }
    }
}
