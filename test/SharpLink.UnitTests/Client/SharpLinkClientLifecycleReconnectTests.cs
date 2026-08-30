using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleReconnectSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLifecycleReconnectTests
{
    [Test]
    public async Task DisconnectedReadySessionShouldReconnectWithFreshConnection()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(() => transport.ConnectCount >= 2 && client.State == SharpLinkConnectionState.Ready);

        var second = await transport.WaitForConnectionAsync(1);
        Ensure(!ReferenceEquals(first, second), "reconnect must own a fresh transport connection");
    }

    [Test]
    public async Task FixedReconnectShouldDialOnceAtTheExactProviderBoundary()
    {
        var provider = new ManualTimeProvider();
        var clock = new TimerArmObservingTimeProvider(provider, TimeSpan.FromMilliseconds(100));
        var transport = new SequenceClientTransportFactory();
        var jitter = new FixedReconnectJitter(TimeSpan.FromMilliseconds(100));
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(clock);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseReconnectJitterForTesting(jitter);
        });
        try
        {
            await client.ConnectAsync();
            var first = GetOnlyReadyConnection(client);
            first.Session.NotifyDisconnected(new IOException("fixed reconnect test disconnect"));
            var ready = GetReadySignalTask(client);
            await clock.ExpectedTimerArmed;
            Ensure(client.State == SharpLinkConnectionState.Reconnecting &&
                   jitter.ScaleTwentyPercentCalls == 1 &&
                   provider.EarliestTimerTimestamp == TimeSpan.FromMilliseconds(100).Ticks,
                "the fixed reconnect worker must enter its provider delay");

            provider.Advance(TimeSpan.FromMilliseconds(100).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();
            Ensure(transport.ConnectCount == 1,
                "the fixed reconnect worker must not dial one provider tick before its delay");

            provider.Advance(TimeSpan.FromTicks(1));
            await ready;
            Ensure(transport.ConnectCount == 2 &&
                   client.State == SharpLinkConnectionState.Ready &&
                   client.ReadyConnectionCount == 1,
                "the fixed reconnect worker must publish one connection at exact equality");
            Ensure(jitter.ScaleTwentyPercentCalls == 1 && transport.ConnectionCount == 2,
                "one disconnect signal must own exactly one fixed reconnect delay and dial");
        }
        finally
        {
            await client.StopAsync();
        }

        Ensure(provider.ActiveTimerCount == 0,
            "fixed reconnect shutdown must dispose all provider timers");
    }

    [Test]
    public async Task FixedReconnectStopAtDueBoundaryShouldDrainTimerAndWorkerOnce()
    {
        var provider = new ManualTimeProvider();
        var clock = new TimerArmObservingTimeProvider(provider, TimeSpan.FromMilliseconds(100));
        var transport = new SequenceClientTransportFactory();
        var jitter = new FixedReconnectJitter(TimeSpan.FromMilliseconds(100));
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(clock);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseReconnectJitterForTesting(jitter);
        });
        await client.ConnectAsync();
        GetOnlyReadyConnection(client).Session.NotifyDisconnected(
            new IOException("fixed reconnect stop race disconnect"));
        await clock.ExpectedTimerArmed;
        Ensure(jitter.ScaleTwentyPercentCalls == 1 &&
               provider.EarliestTimerTimestamp == TimeSpan.FromMilliseconds(100).Ticks,
            "the fixed reconnect race must arm its provider delay");

        provider.Advance(TimeSpan.FromMilliseconds(100));
        await client.StopAsync();

        var snapshot = client.FrameworkTaskSnapshotForDiagnostics;
        Ensure(transport.ConnectCount is 1 or 2,
            "the due/stop race may admit at most the single boundary dial");
        Ensure(jitter.ScaleTwentyPercentCalls == 1,
            "the due/stop race must not create a replacement reconnect worker");
        Ensure(snapshot.IsSealed && snapshot.IsDrained && snapshot.ActiveTasks == 0,
            "stop at the reconnect due boundary must drain the supervised worker");
        Ensure(provider.ActiveTimerCount == 0,
            "stop at the reconnect due boundary must release every provider timer");
    }

    [Test]
    public async Task StaticClusterReconnectShouldBeSingleFlightAtTheProviderBoundary()
    {
        var provider = new ManualTimeProvider();
        var clock = new TimerArmObservingTimeProvider(provider, TimeSpan.FromMilliseconds(100));
        var firstFactory = new SequenceClientTransportFactory();
        var secondFactory = new SequenceClientTransportFactory();
        var jitter = new FixedReconnectJitter(TimeSpan.FromMilliseconds(100));
        var endpoints = new[]
        {
            new StaticEndpointConfiguration(CreateEndpoint("static-first", 5001), firstFactory),
            new StaticEndpointConfiguration(CreateEndpoint("static-second", 5002), secondFactory)
        };
        var client = ClientBuilderTestHelper.BuildStatic(endpoints, builder =>
        {
            builder.UseTimeProvider(clock);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseCluster(options =>
            {
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            });
            builder.UseReconnectJitterForTesting(jitter);
        });
        try
        {
            await client.ConnectAsync();
            GetClusterReadyConnection(client, "static-first").Session.NotifyDisconnected(
                new IOException("static reconnect test disconnect"));
            await clock.ExpectedTimerArmed;
            Ensure(jitter.AddQuarterWindowCalls == 1 &&
                   provider.EarliestTimerTimestamp == TimeSpan.FromMilliseconds(100).Ticks,
                "the static endpoint must arm its single reconnect worker");
            var reconnect = GetStaticReconnectTask(client, "static-first");

            provider.Advance(TimeSpan.FromMilliseconds(100).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();
            Ensure(firstFactory.ConnectCount == 1 && secondFactory.ConnectCount == 0,
                "static reconnect must not dial either the disconnected endpoint or an unrelated endpoint before its provider boundary");

            provider.Advance(TimeSpan.FromTicks(1));
            await reconnect;
            Ensure(firstFactory.ConnectCount == 2 && client.ReadyConnectionCount == 1,
                "static reconnect must restore the endpoint at exact equality");
            Ensure(jitter.AddQuarterWindowCalls == 1 && secondFactory.ConnectCount == 0,
                "static reconnect must remain per-endpoint single-flight");
        }
        finally
        {
            await client.StopAsync();
        }

        Ensure(provider.ActiveTimerCount == 0,
            "static cluster stop must release reconnect and connection timers");
    }

    [Test]
    public async Task DynamicClusterReconnectShouldBeSingleFlightAtTheProviderBoundary()
    {
        var provider = new ManualTimeProvider();
        var clock = new TimerArmObservingTimeProvider(provider, TimeSpan.FromMilliseconds(100));
        var transport = new SequenceClientTransportFactory();
        var jitter = new FixedReconnectJitter(TimeSpan.FromMilliseconds(100));
        var resolver = new ChannelSnapshotResolver(new SharpLinkEndpointSnapshot(
            1,
            [CreateEndpoint("dynamic-provider", 5003)]));
        var client = ClientBuilderTestHelper.BuildDynamic(resolver, _ => transport, builder =>
        {
            builder.UseTimeProvider(clock);
            builder.UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
            builder.UseCluster(options =>
            {
                options.MaxEndpoints = 1;
                options.MinReadyEndpoints = 1;
                options.MaxConnections = 1;
                options.MaxConnectionsPerEndpoint = 1;
            });
            builder.UseReconnectJitterForTesting(jitter);
        });
        try
        {
            await client.ConnectAsync();
            GetClusterReadyConnection(client, "dynamic-provider").Session.NotifyDisconnected(
                new IOException("dynamic reconnect test disconnect"));
            await clock.ExpectedTimerArmed;
            Ensure(jitter.AddQuarterWindowCalls == 1 &&
                   provider.EarliestTimerTimestamp == TimeSpan.FromMilliseconds(100).Ticks,
                "the dynamic endpoint must arm its single reconnect worker");
            var reconnect = GetDynamicReconnectTask(client, "dynamic-provider");

            provider.Advance(TimeSpan.FromMilliseconds(100).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();
            Ensure(transport.ConnectCount == 1,
                "dynamic reconnect must not dial before its provider boundary");

            provider.Advance(TimeSpan.FromTicks(1));
            await reconnect;
            Ensure(transport.ConnectCount == 2 && client.ReadyConnectionCount == 1,
                "dynamic reconnect must restore the endpoint at exact equality");
            Ensure(jitter.AddQuarterWindowCalls == 1 && transport.ConnectionCount == 2,
                "dynamic reconnect must remain single-flight for one endpoint generation");
        }
        finally
        {
            await client.StopAsync();
        }

        Ensure(resolver.DisposeCount == 1,
            "dynamic client stop must dispose its resolver exactly once");
        Ensure(provider.ActiveTimerCount == 0,
            "dynamic cluster stop must release resolver, reconnect, and connection timers");
    }

    [Test]
    [NotInParallel]
    public async Task ImmediatelyDrainedReconnectShouldNotLoseTheNextReconnectSignal()
    {
        const int immediatelyDrainedReconnects = 8;
        var transport = new SequenceClientTransportFactory(immediatelyDrainedReconnects);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var first = await transport.WaitForConnectionAsync(0);

        await first.DisposeAsync();
        await WaitUntilAsync(
            () => transport.ConnectCount >= immediatelyDrainedReconnects + 2 &&
                  client.State == SharpLinkConnectionState.Ready,
            () => $"reconnect stalled after {transport.ConnectCount} attempts in state {client.State} " +
                  $"with {client.ReadyConnectionCount} ready connections",
            TimeSpan.FromSeconds(10));

        Ensure(client.ReadyConnectionCount == 1,
            "a reconnect drained before its worker exits must schedule a replacement");
    }

    [Test]
    public async Task FailedExpansionShouldHandZeroReadyPoolToReconnectWorker()
    {
        var transport = new SequenceClientTransportFactory(failedConnectsAfterInitial: 1);
        var loggerFactory = new CaptureLoggerFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseLoggerFactory(loggerFactory);
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 1;
                options.MaxConnections = 2;
            });
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
                entry is
                {
                    Level: LogLevel.Warning, EventId.Id: LogEvents.Client.ConnectionAttemptFailed,
                    Exception: SocketException
                }) >= 0,
            "the recoverable expansion failure should remain observable through its warning event");
    }

    [Test]
    public async Task ConnectShouldEstablishConfiguredMinimumPoolSize()
    {
        var transport = new SequenceClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseConnectionPool(options =>
            {
                options.MinConnections = 2;
                options.MaxConnections = 2;
            }));

        await client.ConnectAsync();
        Ensure(transport.ConnectCount == 2, "minimum pool should be ready when ConnectAsync returns");
        Ensure(client.ReadyConnectionCount == 2, "ready pool size");
    }
}
