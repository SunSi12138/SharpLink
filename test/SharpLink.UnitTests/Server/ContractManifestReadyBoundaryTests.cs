using System.Buffers;
using System.Net;
using System.Reflection;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

[NotInParallel("rollback-plugin")]
public class ContractManifestReadyBoundaryTests
{
    [Test]
    public async Task BootstrapManifestWriteMustObserveReadyRegistryBoundary()
    {
        await RollbackState.TestIsolation.WaitAsync();
        var connection = new TestTransportConnection();
        var listener = new SingleConnectionListener(connection);
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener)
            .Build();
        var registry = (ServerConnectionRegistry)typeof(SharpLinkServer)
            .GetField("_connectionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!;
        var mutationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateAtBootstrapWrite = ServerConnectionLifecycleState.Handshaking;
        SharpLinkAssemblyRegistrationResult registrationResult = default;

        connection.RunOnNextOutputBufferRequest(() =>
        {
            // The first packet is HandshakeResponse. Arm the next packet write, which must be
            // the single bootstrap ContractManifest for a manifest-capable connection.
            connection.RunOnNextOutputBufferRequest(() =>
            {
                try
                {
                    var active = registry.SnapshotActive();
                    if (active.Length != 1)
                        throw new InvalidOperationException($"expected one active connection, found {active.Length}");
                    var serverConnection = active[0];
                    stateAtBootstrapWrite = serverConnection.LifecycleState;
                    registrationResult = server.RegisterAssembly(typeof(RollbackMarker).Assembly);
                    mutationObserved.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    mutationObserved.TrySetException(exception);
                }
            });
        });

        var runTask = server.RunAsync().AsTask();
        try
        {
            await WaitForConnectionAsync(registry);

            var requestPayload = new PooledByteBufferWriter();
            var request = new ProtocolV2HandshakeRequest(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.ContractManifest,
                ProtocolV2Capabilities.ContractManifest,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024,
                ReadOnlyMemory<byte>.Empty);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                requestPayload,
                request,
                new SharpLinkProtocolOptions());
            await connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0,
                requestPayload.WrittenMemory);

            await connection.WaitForSentFrame(ProtocolV2FrameType.HandshakeResponse)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var bootstrapFrame = await connection.WaitForSentFrame(ProtocolV2FrameType.ContractManifest)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await mutationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Ensure(stateAtBootstrapWrite == ServerConnectionLifecycleState.Ready,
                "bootstrap manifest must not become writable while the server connection is still Handshaking");
            Ensure(registrationResult.Succeeded,
                "the deterministic bootstrap-window mutation must publish successfully");

            var bootstrap = ProtocolV2ContractManifestCodec.Read(
                new ReadOnlySequence<byte>(bootstrapFrame.Payload),
                new SharpLinkProtocolOptions());
            var refreshFrame = await connection.WaitForSentFrame(ProtocolV2FrameType.ContractManifest)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var refresh = ProtocolV2ContractManifestCodec.Read(
                new ReadOnlySequence<byte>(refreshFrame.Payload),
                new SharpLinkProtocolOptions());

            Ensure(refresh.Generation > bootstrap.Generation,
                "a registry mutation after the Ready/bootstrap boundary must be published as a later generation");
        }
        finally
        {
            try { await server.StopAsync(TimeSpan.Zero); } catch { }
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await server.DisposeAsync(); } catch { }
            RollbackState.TestIsolation.Release();
        }
    }

    private static async Task WaitForConnectionAsync(ServerConnectionRegistry registry)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (registry.Count == 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("server did not publish the accepted connection");
            await Task.Delay(1);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class SingleConnectionListener(TestTransportConnection connection) : IServerTransportListener
    {
        private int _accepted;

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _accepted, 1) == 0)
                return connection;

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
