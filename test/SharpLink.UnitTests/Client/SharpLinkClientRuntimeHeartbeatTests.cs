using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleHeartbeatSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientRuntimeHeartbeatTests
{
    [Test]
    public async Task ShorterIntervalMustWakeAndRescheduleTheExistingHeartbeatLoop()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        });
        await client.ConnectAsync();

        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
        await YieldUntilAsync(
            () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
            "initial heartbeat schedule was not armed at ten seconds");

        provider.Advance(TimeSpan.FromSeconds(3));
        client.UpdateHeartbeatInterval(TimeSpan.FromSeconds(4));
        var snapshot = client.GetHeartbeatConfigurationSnapshot();
        Ensure(snapshot.Generation == 1 &&
               snapshot.Interval == TimeSpan.FromSeconds(4) &&
               snapshot.Timeout == TimeSpan.FromSeconds(30),
            "interval replacement must publish one complete heartbeat generation");
        await YieldUntilAsync(
            () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(4).Ticks,
            "shortening the interval must cancel the old wait and rearm from the last Ping anchor");

        provider.Advance(TimeSpan.FromSeconds(1));
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
        await YieldUntilAsync(
            () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(8).Ticks,
            "the same heartbeat loop must continue from the new four-second interval");
    }

    [Test]
    public async Task LongerIntervalMustNotAllowTheOldTimerToSendAnEarlyPing()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        });
        await client.ConnectAsync();

        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
        await YieldUntilAsync(
            () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(5).Ticks,
            "initial five-second heartbeat schedule");

        provider.Advance(TimeSpan.FromSeconds(2));
        client.UpdateHeartbeatInterval(TimeSpan.FromSeconds(10));
        await YieldUntilAsync(
            () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
            "lengthening the interval must replace rather than retain the five-second schedule");

        provider.Advance(TimeSpan.FromSeconds(3));
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Ping,
                TimeSpan.FromMilliseconds(100)),
            "the cancelled five-second schedule must not emit a stale Ping");

        provider.Advance(TimeSpan.FromSeconds(5));
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
    }

    [Test]
    public async Task ShorterTimeoutMustUseExistingActivityAgeAndExpirePromptly()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        });
        try
        {
            await client.ConnectAsync();
            var connection = GetOnlyReadyConnection(client);
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(5).Ticks,
                "initial heartbeat timer");

            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
                "second heartbeat timer");
            provider.Advance(TimeSpan.FromSeconds(3));

            var sessionStopped = GetSessionStoppedTask(connection.Session);
            client.UpdateHeartbeatTimeout(TimeSpan.FromSeconds(7));
            await sessionStopped.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => connection.State == ClientConnectionState.Closed,
                () => "shortened heartbeat timeout did not finish supervised connection cleanup");
            Ensure(connection.State == ClientConnectionState.Closed,
                "shrinking timeout below retained peer inactivity must close without waiting for the old interval");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task LongerTimeoutMustExtendFromExistingActivityWithoutResettingIt()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        });
        try
        {
            await client.ConnectAsync();
            var connection = GetOnlyReadyConnection(client);
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(5).Ticks,
                "initial heartbeat timer");

            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            await YieldUntilAsync(
                () => provider.EarliestTimerTimestamp == TimeSpan.FromSeconds(10).Ticks,
                "ten-second heartbeat timer");
            provider.Advance(TimeSpan.FromSeconds(3));
            client.UpdateHeartbeatTimeout(TimeSpan.FromSeconds(20));

            provider.Advance(TimeSpan.FromSeconds(2));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            provider.Advance(TimeSpan.FromSeconds(5));
            _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
            Ensure(connection.State == ClientConnectionState.Ready,
                "elapsed activity equal to the increased timeout must remain healthy");

            var sessionStopped = GetSessionStoppedTask(connection.Session);
            provider.Advance(TimeSpan.FromSeconds(5));
            await sessionStopped.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => connection.State == ClientConnectionState.Closed,
                () => "extended heartbeat timeout did not finish supervised connection cleanup");
            Ensure(connection.State == ClientConnectionState.Closed,
                "the increased timeout must still be measured from the original peer activity, not reset by the update");
        }
        finally
        {
            await client.StopAsync();
        }
    }

    [Test]
    public async Task InvalidOrStoppedUpdatesMustNotPublishOrLeaveTimersBehind()
    {
        var provider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport, builder =>
        {
            builder.UseTimeProvider(provider);
            builder.UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        });
        var initial = client.GetHeartbeatConfigurationSnapshot();

        EnsureThrows<ArgumentOutOfRangeException>(() => client.UpdateHeartbeatInterval(TimeSpan.Zero));
        EnsureThrows<ArgumentException>(() => client.UpdateHeartbeatInterval(TimeSpan.FromSeconds(10)));
        EnsureThrows<ArgumentException>(() => client.UpdateHeartbeatTimeout(TimeSpan.FromSeconds(5)));
        EnsureThrows<ArgumentException>(() => client.UpdateHeartbeat(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(7)));
        Ensure(client.GetHeartbeatConfigurationSnapshot() == initial,
            "invalid candidates must not advance the heartbeat generation");

        await client.ConnectAsync();
        _ = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Ping);
        client.UpdateHeartbeat(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
        var published = client.GetHeartbeatConfigurationSnapshot();
        await client.StopAsync();

        EnsureThrows<InvalidOperationException>(() => client.UpdateHeartbeatInterval(TimeSpan.FromSeconds(3)));
        EnsureThrows<InvalidOperationException>(() => client.UpdateHeartbeatTimeout(TimeSpan.FromSeconds(15)));
        EnsureThrows<InvalidOperationException>(() => client.UpdateHeartbeat(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)));
        Ensure(client.GetHeartbeatConfigurationSnapshot() == published,
            "Stop-rejected updates must leave the last heartbeat generation unchanged");
        Ensure(provider.ActiveTimerCount == 0,
            "configuration wakes and Stop must leave no heartbeat scheduling timer behind");
    }

    private static void EnsureThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
