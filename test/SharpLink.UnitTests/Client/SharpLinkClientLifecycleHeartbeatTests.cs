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
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleHeartbeatSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLifecycleHeartbeatTests
{
    [Test]
    public async Task FutureWallClockActivityShouldNotSuppressHeartbeatTimeout()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseHeartbeat(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30)));
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
    public async Task ClientHeartbeatShouldSendImmediatelyAndCloseOnlyAfterPostTimeoutCheck()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var jitter = new FixedReconnectJitter(TimeSpan.FromMilliseconds(100));
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            builder.UseReconnectJitterForTesting(jitter);
        });
        try
        {
            await client.ConnectAsync();
            var connection = GetOnlyReadyConnection(client);

            var immediate = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            Ensure(immediate.Type == ProtocolV2FrameType.Ping && provider.GetTimestamp() == 0,
                "the heartbeat loop must send its first Ping before advancing the provider");
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(5).Ticks,
                "the immediate Ping did not arm the first provider heartbeat interval");

            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
                "the first healthy check did not rearm its provider interval");
            Ensure(connection.State == ClientConnectionState.Ready && connection.Session.IsConnected,
                "the first provider heartbeat check must keep the connection ready");

            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(15).Ticks,
                "the equality check did not rearm its provider interval");
            Ensure(connection.Session.TimeSinceLastActivity == TimeSpan.FromSeconds(10) &&
                   connection.State == ClientConnectionState.Ready && connection.Session.IsConnected,
                "elapsed equal to the heartbeat timeout must remain healthy and send the next Ping");

            var sessionStopped = GetSessionStoppedTask(connection.Session);
            provider.Advance(TimeSpan.FromSeconds(5));
            await sessionStopped;
            Ensure(connection.State == ClientConnectionState.Closed && !connection.Session.IsConnected,
                "the first check after the timeout boundary must close the silent connection");
            Ensure(transport.ConnectCount == 1,
                "the timeout must not dial again before the reconnect provider delay");
        }
        finally
        {
            await client.StopAsync();
        }

        var snapshot = client.FrameworkTaskSnapshotForDiagnostics;
        Ensure(snapshot.IsSealed && snapshot.IsDrained && snapshot.ActiveTasks == 0,
            "heartbeat timeout cleanup must drain every supervised loop");
        Ensure(provider.ActiveTimerCount == 0,
            "heartbeat timeout and stop must dispose heartbeat, deadline, and reconnect timers");
    }

    [Test]
    public async Task FullSendQueueHeartbeatShouldWaitForCapacityWithoutClosingConnection()
    {
        await using var client = ClientBuilderTestHelper.Build(
            new NonConnectingFactory(),
            builder => builder.UseRuntime(static options => options.FlowControl.MaxSendQueueBytes = 1));
        var context = (SharpLinkRuntimeContext)client.RuntimeContext;
        var input = new Pipe();
        var output = new BlockingFlushPipeWriter();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "heartbeat-backpressure",
            input.Reader,
            output,
            RpcSessionTestFixture.ClientOptions(context));
        using var connectionCancellation = new CancellationTokenSource();
        await using var connection = new ClientConnection(
            client,
            session,
            connectionCancellation,
            8,
            context);
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
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "control-frame-fast-path",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
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
}
