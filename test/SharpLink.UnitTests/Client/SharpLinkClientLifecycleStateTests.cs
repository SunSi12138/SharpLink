using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public class SharpLinkClientLifecycleStateTests
{
    [Test]
    public async Task ConcurrentConnectsShouldShareOneAttemptAndReadyLoopSet()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

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
    public async Task FutureWallClockActivityShouldNotSuppressHeartbeatTimeout()
    {
        var transport = new TestClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(30));
        await client.ConnectAsync();
        var readyConnectionsField = typeof(SharpLinkClient).GetField(
            "_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find ready connection field");
        var connection = ((ClientConnection[])readyConnectionsField.GetValue(client)!)[0];

        connection.Session.LastActive = DateTime.UtcNow.AddDays(1);

        await WaitUntilAsync(
            () => connection.State == ClientConnectionState.Closed,
            () => $"heartbeat did not close the silent connection; state={connection.State}");
    }

    [Test]
    public async Task FullSendQueueHeartbeatShouldWaitForCapacityWithoutClosingConnection()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.FlowControl.MaxSendQueueBytes = 1)
            .Build();
        await using var client = new SharpLinkClient(
            new NonConnectingFactory(),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(2),
            runtimeContext: context);
        var input = new Pipe();
        var output = new BlockingFlushPipeWriter();
        var session = new RpcSession(
            "heartbeat-backpressure",
            input.Reader,
            output,
            static () => { },
            static () => true);
        session.BindRuntimeContext(context);
        using var connectionCancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            client,
            session,
            connectionCancellation,
            8,
            context.Codecs);
        var runHeartbeat = typeof(SharpLinkClient).GetMethod(
            "RunHeartbeatSendLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Client heartbeat wrapper");

        session.SendHealthCheck(99);
        await output.FirstFlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var heartbeat = (Task)runHeartbeat.Invoke(
            client,
            [connection, connectionCancellation.Token])!;

        try
        {
            Ensure(!heartbeat.IsCompleted,
                "a full send queue must leave Ping on the asynchronous capacity-wait path");
            Ensure(connection.State == ClientConnectionState.Ready && session.IsConnected,
                "heartbeat queue pressure must not close the ready client connection");

            output.ReleaseFlush();
            await output.SecondFlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            EnsureTimestampFrame(
                output.WrittenMemory,
                context.Protocol,
                ProtocolV2FrameType.Ping,
                expectedTimestamp: null);

            connectionCancellation.Cancel();
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(connection.State == ClientConnectionState.Ready && session.IsConnected,
                "capacity recovery and expected loop cancellation must keep the connection healthy");
        }
        finally
        {
            output.ReleaseFlush();
            connectionCancellation.Cancel();
            try
            {
                await heartbeat.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task AvailableControlFrameQueueShouldKeepSynchronousFastPath()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(static options => options.FlowControl.MaxSendQueueBytes = 1024)
            .Build();
        var input = new Pipe();
        var output = new Pipe();
        await using var session = new RpcSession(
            "control-frame-fast-path",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        session.BindRuntimeContext(context);
        const long pongTimestamp = 0x0102_0304_0506_0708;

        var ping = session.SendPingWithBackpressureAsync();
        var pong = session.SendPongWithBackpressureAsync(pongTimestamp);
        var health = session.SendHealthResponseWithBackpressureAsync(17, SharpLinkHealthStatus.Ready);

        Ensure(ping.IsCompletedSuccessfully,
            "an available queue must preserve synchronous Ping completion");
        Ensure(pong.IsCompletedSuccessfully,
            "the shared timestamp primitive must preserve synchronous Pong completion");
        Ensure(health.IsCompletedSuccessfully,
            "the shared control-frame primitive must preserve synchronous HealthResponse completion");
        await ping;
        await pong;
        await health;
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        EnsureTimestampFrame(read.Buffer, context.Protocol, ProtocolV2FrameType.Ping, expectedTimestamp: null);
        EnsureTimestampFrame(read.Buffer, context.Protocol, ProtocolV2FrameType.Pong, pongTimestamp);
        EnsureHealthResponseFrame(read.Buffer, context.Protocol, 17, SharpLinkHealthStatus.Ready);
        output.Reader.AdvanceTo(read.Buffer.End);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SharedFixedConnectShouldSurviveFirstWaiterCancellation()
    {
        var transport = new BlockingInitialTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
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
        await using (var staticClient = SharpClientBuilder.Create()
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
        await using var dynamicClient = SharpClientBuilder.Create()
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
        var client = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var shutdownField = typeof(SharpLinkClient).GetField(
            "_shutdownCts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find client shutdown source");
        var shutdown = (CancellationTokenSource)shutdownField.GetValue(client)!;
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new ManualResetEventSlim();
        using var registration = shutdown.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });
        var stopReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = Task.Run(() =>
        {
            var stop = client.StopAsync().AsTask();
            stopReturned.TrySetResult(stop);
        });
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Ensure(stopReturned.Task.IsCompleted,
                "an async StopAsync call must return before a blocking cancellation callback finishes");
        }
        finally
        {
            releaseCallback.Set();
        }

        await invocation.WaitAsync(TimeSpan.FromSeconds(2));
        await (await stopReturned.Task).WaitAsync(TimeSpan.FromSeconds(2));
        releaseCallback.Dispose();
    }

    [Test]
    public async Task FailedConnectShouldPreservePrimaryAndCleanupFailures()
    {
        await using var client = new SharpLinkClient(
            new CleanupFailingHandshakeTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));

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
    public async Task InitialPoolRollbackShouldPreserveConnectAndCleanupFailures()
    {
        var client = new SharpLinkClient(
            new InitialPoolRollbackFailingTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 2,
                MaxConnections = 2
            });

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
        var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
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

    [Test]
    public async Task StopShouldPreserveAnUnexpectedCompletedReconnectFailure()
    {
        var client = new SharpLinkClient(
            new NonConnectingFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var reconnectTaskField = typeof(SharpLinkClient).GetField(
            "_reconnectTask",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find client reconnect task");
        reconnectTaskField.SetValue(
            client,
            Task.FromException(new InvalidOperationException("unexpected reconnect cleanup failure")));

        Exception failure;
        try
        {
            await client.StopAsync();
            throw new Exception("expected stop failure");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "unexpected reconnect cleanup failure" }),
            "shutdown cancellation must not hide an unexpected completed reconnect failure");
        Ensure(client.State == SharpLinkConnectionState.Stopped,
            "client cleanup must still reach the stopped state when it reports the failure");
    }

    [Test]
    public async Task BackgroundJoinShouldNotHideAnUnexpectedNestedFailure()
    {
        await using var client = new SharpLinkClient(
            new NonConnectingFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = Task.WhenAll(expected.Task, unexpected.Task);
        typeof(SharpLinkClient).GetMethod(
                "TrackBackgroundTask",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [mixed]);
        var wait = typeof(SharpLinkClient).GetMethod(
            "WaitForBackgroundTasksAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Client background task join");
        var joined = (Task)wait.Invoke(client, null)!;
        await Task.Yield();
        expected.TrySetException(new IOException("expected background transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected background nested failure"));

        Exception? failure = null;
        try
        {
            await joined;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is not null && ContainsException(failure, static exception =>
                exception is InvalidOperationException { Message: "unexpected background nested failure" }),
            "an expected background close must not hide an unexpected nested task failure");
    }

    [Test]
    public async Task StaticClusterWorkerJoinShouldNotHideAnUnexpectedNestedFailure()
    {
        await using var client = (SharpLinkClient)SharpClientBuilder.Create()
            .UseEndpoints(
                [CreateEndpoint("first", 5001), CreateEndpoint("second", 5002)],
                _ => new NonConnectingFactory())
            .Build();
        var cluster = typeof(SharpLinkClient).GetField(
                "_cluster",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)
            ?? throw new Exception("cannot find static cluster runtime");
        var endpoints = (Array)(cluster.GetType().GetField(
                "_endpoints",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cluster)
            ?? throw new Exception("cannot find static cluster endpoints"));
        var firstEndpoint = endpoints.GetValue(0)
            ?? throw new Exception("cannot find first static endpoint state");
        var reconnectTask = firstEndpoint.GetType().GetProperty("ReconnectTask")
            ?? throw new Exception("cannot find static reconnect worker");
        var expected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        reconnectTask.SetValue(firstEndpoint, Task.WhenAll(expected.Task, unexpected.Task));
        var failures = new List<Exception>();
        var wait = cluster.GetType().GetMethod(
            "WaitForWorkersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find static cluster worker join");
        var joined = (Task)wait.Invoke(cluster, [failures])!;
        await Task.Yield();
        expected.TrySetException(new IOException("expected static worker transport closure"));
        unexpected.TrySetException(new InvalidOperationException("unexpected static worker nested failure"));

        await joined;

        var retainedUnexpected = false;
        for (var index = 0; index < failures.Count; index++)
        {
            retainedUnexpected |= ContainsException(failures[index], static exception =>
                exception is InvalidOperationException { Message: "unexpected static worker nested failure" });
        }
        Ensure(retainedUnexpected,
            "an expected static worker close must not hide an unexpected nested task failure");
    }

    [Test]
    public async Task DisconnectedReadySessionShouldReconnectWithFreshConnection()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(() => transport.ConnectCount >= 2 && client.State == SharpLinkConnectionState.Ready);

        var second = await transport.WaitForConnectionAsync(1);
        Ensure(!ReferenceEquals(first, second), "reconnect must own a fresh transport connection");
    }

    [Test]
    public async Task ImmediatelyDrainedReconnectShouldNotLoseTheNextReconnectSignal()
    {
        const int immediatelyDrainedReconnects = 8;
        var transport = new SequenceClientTransportFactory(immediatelyDrainedReconnects);
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(
            () => transport.ConnectCount >= immediatelyDrainedReconnects + 2 &&
                  client.State == SharpLinkConnectionState.Ready,
            () => $"reconnect stalled after {transport.ConnectCount} attempts in state {client.State} " +
                  $"with {client.ReadyConnectionCount} ready connections");

        Ensure(client.ReadyConnectionCount == 1,
            "a reconnect drained before its worker exits must schedule a replacement");
    }

    [Test]
    public async Task FailedExpansionShouldHandZeroReadyPoolToReconnectWorker()
    {
        var transport = new SequenceClientTransportFactory(failedConnectsAfterInitial: 1);
        var loggerFactory = new CaptureLoggerFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            loggerFactory,
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 1,
                MaxConnections = 2
            });
        await client.ConnectAsync();
        var firstConnection = await transport.WaitForConnectionAsync(0);

        var firstCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        _ = await firstConnection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var secondCall = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        await WaitUntilAsync(() => transport.ConnectCount >= 2);

        await firstConnection.DisposeAsync();
        await ObserveConnectionFailureAsync(firstCall);
        await ObserveConnectionFailureAsync(secondCall);
        await WaitUntilAsync(
            () => transport.ConnectCount >= 3 &&
                  client.ReadyConnectionCount == 1 &&
                  client.State == SharpLinkConnectionState.Ready,
            () => $"failed expansion stranded the client after {transport.ConnectCount} attempts " +
                  $"in state {client.State} with {client.ReadyConnectionCount} ready connections");
        Ensure(loggerFactory.Entries.FindIndex(static entry => entry.Level == LogLevel.Error) < 0,
            "a recoverable expansion failure must not be reported as an unhandled background error");
        Ensure(loggerFactory.Entries.FindIndex(static entry =>
                entry is { Level: LogLevel.Warning, EventId.Id: LogEvents.Client.ConnectionAttemptFailed,
                    Exception: SocketException }) >= 0,
            "the recoverable expansion failure should remain observable through its warning event");
    }

    [Test]
    public async Task ConnectShouldEstablishConfiguredMinimumPoolSize()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 2,
                MaxConnections = 2
            });

        await client.ConnectAsync();
        Ensure(transport.ConnectCount == 2, "minimum pool should be ready when ConnectAsync returns");
        Ensure(client.ReadyConnectionCount == 2, "ready pool size");
    }

    [Test]
    public async Task ConcurrentClientConnectionDisposersShouldAwaitPhysicalCleanup()
    {
        await using var owner = new SharpLinkClient(
            new NonConnectingFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var transport = new BlockingDisposeConnection();
        var connection = new ClientConnection(
            owner,
            new RpcSession(transport),
            new CancellationTokenSource(),
            8,
            context.Codecs);

        var first = connection.DisposeAsync().AsTask();
        await transport.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = connection.DisposeAsync().AsTask();

        Ensure(!second.IsCompleted, "concurrent disposal must await physical transport cleanup");
        transport.ReleaseDispose();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task CancellationCallbackFailureMustNotStrandPendingCalls()
    {
        await using var owner = new SharpLinkClient(
            new NonConnectingFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        using var cancellation = new CancellationTokenSource();
        using var callback = cancellation.Token.Register(
            static () => throw new InvalidOperationException("connection cancellation callback failed"));
        var connection = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            cancellation,
            8,
            context.Codecs);
        var operation = connection.PendingCalls.Rent<int>(out _);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "connection failed");

        try
        {
            connection.Fail(terminal);
            try
            {
                _ = await operation.AsValueTask();
                throw new Exception("expected pending call failure");
            }
            catch (SharpLinkException exception)
            {
                Ensure(ReferenceEquals(exception, terminal), "pending call must retain terminal failure");
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Test]
    public async Task PowerOfTwoChoiceShouldSelectLowerActiveConnection()
    {
        await using var owner = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var context = new SharpLinkRuntimeContextBuilder().Build();
        await using var first = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        await using var second = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        var firstCall1 = first.PendingCalls.Rent<int>(out var firstId1);
        var firstCall2 = first.PendingCalls.Rent<int>(out var firstId2);
        var secondCall = second.PendingCalls.Rent<int>(out var secondId);

        var selected = SharpLinkClient.SelectLeastLoaded([first, second], 0, 1);
        Ensure(ReferenceEquals(selected, second), "power-of-two should select the lower active count");

        var completed = new InvalidOperationException("test completion");
        first.PendingCalls.DispatchError(firstId1, completed);
        first.PendingCalls.DispatchError(firstId2, completed);
        second.PendingCalls.DispatchError(secondId, completed);
        await ObserveFailureAsync(firstCall1.AsValueTask());
        await ObserveFailureAsync(firstCall2.AsValueTask());
        await ObserveFailureAsync(secondCall.AsValueTask());
    }

    [Test]
    public async Task ClusterSelectionShouldFallBackFromAStalePooledConnection()
    {
        await using var owner = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30));
        var context = new SharpLinkRuntimeContextBuilder().Build();
        await using var stale = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        await using var ready = new ClientConnection(
            owner,
            new RpcSession(new TestTransportConnection()),
            new CancellationTokenSource(),
            8,
            context.Codecs);
        stale.Session.NotifyConnected();
        ready.Session.NotifyConnected();
        Ensure(ready.TryBeginUntrackedCall(), "ready connection active-call setup");
        stale.MarkDraining();

        try
        {
            Ensure(ReferenceEquals(
                    SelectClusterConnection("StaticClusterRuntime", 0, stale, ready),
                    ready),
                "static cluster should fall back to an accepting pooled connection");
            Ensure(ReferenceEquals(
                    SelectClusterConnection("DynamicClusterRuntime", 0L, stale, ready),
                    ready),
                "dynamic cluster should fall back to an accepting pooled connection");
        }
        finally
        {
            ready.EndUntrackedCall();
        }
    }

    [Test]
    public async Task AdmissionRetryAfterShouldSurviveAStaleGrantedConnection()
    {
        var policy = new AdmitFirstRejectSecondPolicy(TimeSpan.FromMilliseconds(100));
        await using var client = new SharpLinkClient(
            new TestClientTransportFactory(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            endpointAdmissionPolicy: policy);
        var stateType = typeof(SharpLinkClient).GetNestedType("AttemptOutcomeState", BindingFlags.NonPublic)
            ?? throw new Exception("cannot find attempt outcome state");
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        var state = Activator.CreateInstance(
            stateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [client, method],
            culture: null)
            ?? throw new Exception("cannot create attempt outcome state");
        var tryAcquire = stateType.GetMethod("TryAcquire", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find attempt acquisition");
        var complete = stateType.GetMethod("CompleteWithoutPending", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find attempt completion");
        var shouldHonor = stateType.GetProperty("ShouldHonorAdmissionRetryAfter", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new Exception("cannot find retry-after predicate");
        var first = new SharpLinkEndpointCandidate(CreateEndpoint("first", 5001), 1, 0, generation: 1);
        var second = new SharpLinkEndpointCandidate(CreateEndpoint("second", 5002), 1, 0, generation: 1);

        Ensure((bool)(tryAcquire.Invoke(state, [first]) ?? false), "first endpoint should be admitted");
        complete.Invoke(
            state,
            [
                PendingCallCompletionReason.ConnectionClosed,
                new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "selected connection became stale")
            ]);
        Ensure(!(bool)(tryAcquire.Invoke(state, [second]) ?? true), "second endpoint should be rejected");
        Ensure((bool)(shouldHonor.GetValue(state) ?? false),
            "a stale admitted endpoint must not suppress the current selection retry-after");
    }

    [Test]
    public async Task GoAwayShouldDrainOnlyItsConnectionAndRefillMinimumPool()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            connectionPoolOptions: new SharpLinkConnectionPoolOptions
            {
                MinConnections = 2,
                MaxConnections = 2
            });
        await client.ConnectAsync();
        var drainingConnection = await transport.WaitForConnectionAsync(0);
        await InjectGoAwayAsync(drainingConnection);
        await WaitUntilAsync(() => transport.ConnectCount >= 3 && client.ReadyConnectionCount == 2);

        Ensure(client.State == SharpLinkConnectionState.Ready, "another ready connection should keep the client ready");
    }

    [Test]
    public async Task GoAwayShouldCountAsBreakerFailureWithoutAnActiveCall()
    {
        var transport = new TestClientTransportFactory();
        var endpoint = new SharpLinkEndpoint
        {
            Id = "breaker",
            Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
        };
        var breaker = new SharpLinkCircuitBreaker(new SharpLinkCircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(5),
            HalfOpenMaxCalls = 1
        }.CloneValidated());
        await using var client = new SharpLinkClient(
            transport,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            fixedEndpoint: endpoint,
            endpointAdmissionPolicy: breaker);
        await client.ConnectAsync();

        await InjectGoAwayAsync(transport.Connection);

        var candidate = new SharpLinkEndpointCandidate(endpoint, 0, 0, generation: 0);
        var method = new RpcMethodDescriptor(1, 2, RpcMethodKind.Unary, true, false, false, null);
        await WaitUntilAsync(
            () => !breaker.TryAcquire(candidate, method).IsAllowed,
            () => "GoAway was not recorded as an endpoint infrastructure failure");
    }

    private static async Task InjectGoAwayAsync(TestTransportConnection connection)
    {
        var payload = new PooledByteBufferWriter();
        var lastAccepted = payload.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
        payload.Advance(sizeof(ulong));
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unavailable,
            "rolling restart",
            1024,
            out _);

        await connection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? timeoutMessage = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage?.Invoke() ?? "The expected client state was not reached.");
        }
    }

    private static void EnsureTimestampFrame(
        ReadOnlyMemory<byte> bytes,
        SharpLinkProtocolOptions limits,
        ProtocolV2FrameType expectedType,
        long? expectedTimestamp)
        => EnsureTimestampFrame(
            new ReadOnlySequence<byte>(bytes),
            limits,
            expectedType,
            expectedTimestamp);

    private static void EnsureTimestampFrame(
        ReadOnlySequence<byte> bytes,
        SharpLinkProtocolOptions limits,
        ProtocolV2FrameType expectedType,
        long? expectedTimestamp)
    {
        var remaining = bytes;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
        {
            if (header.Type != expectedType)
                continue;

            Ensure(header.RequestId == 0 && header.Flags == ProtocolV2FrameFlags.None,
                $"{expectedType} must retain its control-frame header");
            Ensure(payload.Length == sizeof(long), $"{expectedType} must retain its timestamp payload");
            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.ToArray());
            Ensure(expectedTimestamp is { } expected
                    ? timestamp == expected
                    : timestamp > 0,
                $"{expectedType} must retain the expected monotonic timestamp");
            return;
        }

        throw new Exception($"{expectedType} frame was not emitted");
    }

    private static void EnsureHealthResponseFrame(
        ReadOnlySequence<byte> bytes,
        SharpLinkProtocolOptions limits,
        ulong expectedRequestId,
        SharpLinkHealthStatus expectedStatus)
    {
        var remaining = bytes;
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
        {
            if (header.Type != ProtocolV2FrameType.HealthResponse)
                continue;

            Ensure(header.RequestId == expectedRequestId && header.Flags == ProtocolV2FrameFlags.None,
                "HealthResponse must retain its request identity and control-frame flags");
            Ensure(ProtocolV2PayloadCodec.ReadHealthResponse(payload).Status == expectedStatus,
                "HealthResponse must retain its exact status payload");
            return;
        }

        throw new Exception($"HealthResponse frame {expectedRequestId} was not emitted");
    }

    private static async Task EnsureCancelledAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected the caller wait to be cancelled");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected a SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static bool ContainsHandshakeTimeout(Exception exception)
    {
        if (exception is SharpLinkException { Code: SharpLinkErrorCode.Unavailable } sharpLink &&
            sharpLink.Message.Contains("handshake timed out", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.InnerExceptions)
            {
                if (ContainsHandshakeTimeout(innerException))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } inner && ContainsHandshakeTimeout(inner);
    }

    private static bool ContainsException(Exception exception, Func<Exception, bool> predicate)
    {
        if (predicate(exception))
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.InnerExceptions)
            {
                if (ContainsException(innerException, predicate))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } inner && ContainsException(inner, predicate);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class BlockingFlushPipeWriter : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly TaskCompletionSource<FlushResult> _flush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushCount;

        internal TaskCompletionSource FirstFlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondFlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;

        public override void Advance(int bytes) => _buffer.Advance(bytes);
        public override void CancelPendingFlush() => _flush.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) => ReleaseFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _flushCount) == 1)
                FirstFlushStarted.TrySetResult();
            else
                SecondFlushStarted.TrySetResult();
            return new ValueTask<FlushResult>(_flush.Task.WaitAsync(cancellationToken));
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

        internal void ReleaseFlush()
            => _flush.TrySetResult(new FlushResult(isCanceled: false, isCompleted: false));
    }

    private static async Task ObserveFailureAsync(ValueTask<int> operation)
    {
        try
        {
            await operation;
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveConnectionFailureAsync(Task<int> operation)
    {
        try
        {
            _ = await operation;
            throw new Exception("expected the disconnected call to fail");
        }
        catch (SharpLinkException exception) when (exception.Code is
            SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable)
        {
        }
    }

    private sealed class SequenceClientTransportFactory : IClientTransportFactory
    {
        private readonly Lock _gate = new();
        private readonly List<TestTransportConnection> _connections = [];
        private readonly int _immediatelyDrainedReconnects;
        private readonly int _failedConnectsAfterInitial;
        private int _connectCount;

        internal SequenceClientTransportFactory(
            int immediatelyDrainedReconnects = 0,
            int failedConnectsAfterInitial = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(immediatelyDrainedReconnects);
            ArgumentOutOfRangeException.ThrowIfNegative(failedConnectsAfterInitial);
            _immediatelyDrainedReconnects = immediatelyDrainedReconnects;
            _failedConnectsAfterInitial = failedConnectsAfterInitial;
        }

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connectNumber = Interlocked.Increment(ref _connectCount);
            if (connectNumber > 1 && connectNumber <= _failedConnectsAfterInitial + 1)
                throw new SocketException((int)SocketError.ConnectionRefused);

            var connection = new TestTransportConnection();
            var payload = new ArrayBufferWriter<byte>();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            if (connectNumber > 1 && connectNumber <= _immediatelyDrainedReconnects + 1)
            {
                using var goAway = new PooledByteBufferWriter();
                var lastAccepted = goAway.GetSpan(sizeof(ulong));
                BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
                goAway.Advance(sizeof(ulong));
                ProtocolV2PayloadCodec.WriteError(
                    goAway,
                    SharpLinkErrorCode.Unavailable,
                    "immediate rolling restart",
                    1024,
                    out _);
                await connection.InjectFrameAsync(
                    ProtocolV2FrameType.GoAway,
                    ProtocolV2FrameFlags.Error,
                    0,
                    goAway.WrittenMemory,
                    cancellationToken);
            }
            lock (_gate)
                _connections.Add(connection);
            return connection;
        }

        public async Task<TestTransportConnection> WaitForConnectionAsync(int index)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                lock (_gate)
                {
                    if (_connections.Count > index)
                        return _connections[index];
                }
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            TestTransportConnection[] connections;
            lock (_gate)
                connections = [.. _connections];
            for (var index = 0; index < connections.Length; index++)
                await connections[index].DisposeAsync();
        }
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();

        internal List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(CaptureLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                    owner.Entries.Add(new LogEntry(logLevel, eventId, exception));
            }
        }
    }

    private readonly record struct LogEntry(LogLevel Level, EventId EventId, Exception? Exception);

    private sealed class BlockingInitialTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TestTransportConnection? _connection;

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var connection = new TestTransportConnection();
            var payload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            _connection = connection;
            return connection;
        }

        internal void ReleaseConnect() => _release.TrySetResult();

        public ValueTask DisposeAsync()
            => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class HangingHandshakeTransportFactory : IClientTransportFactory
    {
        private readonly List<TestTransportConnection> _connections = [];

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var connection = new TestTransportConnection();
            _connections.Add(connection);
            return ValueTask.FromResult<ITransportConnection>(connection);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
                await connection.DisposeAsync();
        }
    }

    private sealed class FixedSnapshotResolver(SharpLinkEndpointSnapshot snapshot) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(snapshot);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupFailingHandshakeTransportFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITransportConnection>(new CleanupFailingConnection());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InitialPoolRollbackFailingTransportFactory : IClientTransportFactory
    {
        private int _connectCount;

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) != 1)
                throw new InvalidOperationException("second connection failed");

            var connection = new TestTransportConnection();
            var payload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(payload, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.None,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024));
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                payload.WrittenMemory,
                cancellationToken);
            return new CleanupFailingReadyConnection(connection);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleanupFailingReadyConnection(TestTransportConnection inner) : ITransportConnection
    {
        public string Id => inner.Id;
        public System.IO.Pipelines.PipeReader Input => inner.Input;
        public System.IO.Pipelines.PipeWriter Output => inner.Output;
        public System.Net.EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public System.Net.EndPoint? RemoteEndPoint => inner.RemoteEndPoint;

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw new InvalidOperationException("ready connection cleanup failed");
        }
    }

    private sealed class CleanupFailingConnection : ITransportConnection
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();

        internal CleanupFailingConnection() => _input.Writer.Complete();

        public string Id { get; } = "cleanup-failing";
        public System.IO.Pipelines.PipeReader Input => _input.Reader;
        public System.IO.Pipelines.PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException("transport cleanup failed"));
    }

    private sealed class BlockingDisposeConnection : ITransportConnection
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id { get; } = "blocking-dispose";
        public System.IO.Pipelines.PipeReader Input => _input.Reader;
        public System.IO.Pipelines.PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            return new ValueTask(_release.Task);
        }

        internal void ReleaseDispose() => _release.TrySetResult();
    }

    private static ClientConnection? SelectClusterConnection(
        string runtimeName,
        object stateIndex,
        ClientConnection stale,
        ClientConnection ready)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Public;
        var runtimeType = typeof(SharpLinkClient).GetNestedType(runtimeName, BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName}");
        var endpointType = runtimeType.GetNestedType("EndpointState", flags)
            ?? throw new Exception($"cannot find {runtimeName}.EndpointState");
        var configuration = new StaticEndpointConfiguration(
            new SharpLinkEndpoint
            {
                Id = "selection",
                Address = new SharpLinkTcpAddress("127.0.0.1", 5001)
            },
            new NonConnectingFactory());
        var endpoint = Activator.CreateInstance(
            endpointType,
            BindingFlags.Instance | flags,
            binder: null,
            args: [configuration, stateIndex],
            culture: null)
            ?? throw new Exception($"cannot create {runtimeName}.EndpointState");
        var readyConnections = endpointType.GetField("_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName} ready connection field");
        readyConnections.SetValue(endpoint, new[] { stale, ready });
        var selectConnection = runtimeType.GetMethod(
            "SelectConnection",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find {runtimeName} selection method");
        return (ClientConnection?)selectConnection.Invoke(null, [endpoint]);
    }

    private static SharpLinkEndpoint CreateEndpoint(string id, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress("127.0.0.1", port)
    };

    private sealed class NonConnectingFactory : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdmitFirstRejectSecondPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }
}
