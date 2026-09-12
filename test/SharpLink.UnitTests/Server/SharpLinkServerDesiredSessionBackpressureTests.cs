using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkServerDesiredSessionBackpressureTests
{
    [Test]
    public Task StalledSessionShouldNotBlockLaterSessionAndShouldAllowSameGenerationRetry()
        => VerifyBoundedRolloutAsync(stopServer: false);

    [Test]
    public Task ServerCancellationShouldCancelPendingRefreshEnqueue()
        => VerifyBoundedRolloutAsync(stopServer: true);

    private static async Task VerifyBoundedRolloutAsync(bool stopServer)
    {
        var clock = new ManualTimeProvider();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTimeProvider(clock)
            .UseRuntime(options => options.FlowControl.MaxSendQueueBytes = 64)
            .UseTransport(new IdleListener())
            .Build();
        var context = GetField<SharpLinkRuntimeContext>(server, "_runtimeContext");
        var registry = GetField<ServerConnectionRegistry>(server, "_connectionRegistry");
        var snapshots = GetField<ConcurrentDictionary<string, SharpLinkServerDesiredSessionSnapshot>>(
            server, "_sessionDesiredSnapshots");
        var pinned = server.DesiredSession;
        var pipes = new Dictionary<string, (Pipe Input, Pipe Output)>();
        for (var index = 0; index < 2; index++)
        {
            var input = new Pipe();
            var output = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                $"refresh-cohort-{index}", input.Reader, output.Writer,
                RpcSessionTestFixture.ServerOptions(context), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(session, ProtocolV2Capabilities.SessionRefresh);
            var connection = new ServerConnectionState(
                session, new RpcSessionGeneratedServerBridge(session),
                new StripedLongMap<ServerCallCancellationState>(context.Concurrency),
                CancellationToken.None, clock);
            Ensure(connection.MarkReady(null) && registry.TryAdd(session.Id, connection),
                "test connections enter the active cohort");
            snapshots[session.Id] = pinned;
            pipes.Add(session.Id, (input, output));
        }
        typeof(SharpLinkServer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, 2); // Running, without starting unrelated listener/heartbeat workers.

        var cohort = registry.Values.ToArray();
        var stalled = cohort[0].Session;
        var healthy = cohort[1].Session;
        var stalledOutput = pipes[stalled.Id].Output.Reader;
        var healthyOutput = pipes[healthy.Id].Output.Reader;
        using var cancellation = new CancellationTokenSource();
        Task? publication = null;
        try
        {
            var writer = stalled.RentFrameWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, 1))
                writer.Write(new byte[64]);
            stalled.SendPacket(writer);
            var held = await stalledOutput.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            stalledOutput.AdvanceTo(held.Buffer.Start, held.Buffer.Start);
            Ensure(stalled.QueuedSendBytes > 64, "first session retains its backpressured frame");

            var baselineTimers = clock.ActiveTimerCount;
            var configuration = new SharpLinkServerDesiredSessionConfiguration
            {
                MaxFramePayloadBytes = pinned.Configuration.MaxFramePayloadBytes / 2
            };
            publication = stopServer
                ? ((ValueTask)typeof(SharpLinkServer).GetMethod(
                    "RequestRollingSessionRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(server, [pinned.Generation + 1, cancellation.Token])!).AsTask()
                : server.PublishDesiredSessionAsync(
                    configuration, SharpLinkSessionRolloutMode.RollingRefresh).AsTask();
            await WaitUntilAsync(() => clock.ActiveTimerCount > baselineTimers);
            Ensure(!publication.IsCompleted && !healthyOutput.TryRead(out _),
                "the first enqueue is pending before the bounded wait expires");

            if (stopServer)
            {
                cancellation.Cancel();
                try
                {
                    await publication.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("server cancellation must cancel publication");
                }
                catch (OperationCanceledException)
                {
                }
                Ensure(clock.ActiveTimerCount == baselineTimers,
                    "server cancellation releases the enqueue timeout timer");
                return;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            var result = server.DesiredSession;
            var refresh = await ReadRefreshAsync(healthyOutput, context.Protocol);
            Ensure(refresh.ServerInstanceId == result.ServerInstanceId &&
                   refresh.DesiredGeneration == result.Generation,
                "the later healthy session receives the published generation");
            Ensure(stalled.IsConnected && stalled.QueuedSendBytes > 64,
                "timeout preserves the live source connection and its pending work");
            Ensure(clock.ActiveTimerCount == baselineTimers,
                "completed scan retains no timeout timers");

            // Free capacity and retry the same generation. The cancelled enqueue must not
            // survive to emit a duplicate refresh once this session starts draining again.
            var pending = await stalledOutput.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            stalledOutput.AdvanceTo(pending.Buffer.End);
            await WaitUntilAsync(() => stalled.QueuedSendBytes == 0 && healthy.QueuedSendBytes == 0);
            var retry = await server.PublishDesiredSessionAsync(
                configuration, SharpLinkSessionRolloutMode.RollingRefresh).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var retriedRefresh = await ReadRefreshAsync(stalledOutput, context.Protocol);
            Ensure(retry == result && retriedRefresh.DesiredGeneration == result.Generation,
                "same-generation retry reaches the formerly stalled session");
            await ReadRefreshAsync(healthyOutput, context.Protocol);
            await WaitUntilAsync(() => stalled.QueuedSendBytes == 0);
            Ensure(!stalledOutput.TryRead(out _), "timed-out enqueue leaves no duplicate notification");
        }
        finally
        {
            cancellation.Cancel();
            GetField<SharpLinkServer.ServerLifecycleCoordinator>(server, "_lifecycle").ForceStopSource.Cancel();
            if (publication is not null)
            {
                try { await publication.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
            foreach (var connection in cohort)
            {
                registry.TryRemove(connection.Session.Id, out _);
                await connection.CloseAsync();
                await pipes[connection.Session.Id].Input.Writer.CompleteAsync();
                await pipes[connection.Session.Id].Output.Reader.CompleteAsync();
            }
        }
    }

    private static async Task<ProtocolV2SessionRefreshRequested> ReadRefreshAsync(
        PipeReader reader, SharpLinkProtocolOptions limits)
    {
        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var buffer = read.Buffer;
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out var payload) &&
               header.Type == ProtocolV2FrameType.SessionRefreshRequested,
            "expected one complete refresh notification");
        var request = ProtocolV2PayloadCodec.ReadSessionRefreshRequested(payload);
        Ensure(buffer.IsEmpty, "one scan emits exactly one notification per session");
        reader.AdvanceTo(buffer.End);
        return request;
    }

    private static T GetField<T>(SharpLinkServer server, string name)
        => (T)typeof(SharpLinkServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
