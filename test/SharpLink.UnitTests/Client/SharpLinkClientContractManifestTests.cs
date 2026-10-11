using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractManifestTests : SharpLinkMultiClusterClientTestBase
{
    private const long OrdersContractId = 8_101;

    [Test]
    public async Task EqualRpcAssemblyHashShouldAllowContractAcquisition()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);

        await client.ConnectAsync();
        var proxy = client.Get<IOrdersContract>();

        Ensure(proxy is OrdersProxy,
            "an exactly matching remote RpcAssemblyHash must allow contract acquisition");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "contract acquisition itself must not emit an RPC Request frame");
    }

    [Test]
    public async Task MismatchedRemoteManifestShouldNotAffectLocalProxyCreation()
    {
        var transport = CreateTransport(new RpcHash128(1, 2));
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var proxy = client.Get<IOrdersContract>();
        Ensure(proxy is OrdersProxy,
            "Get must depend only on the local descriptor, even when the remote hash differs");
        Ensure(ReferenceEquals(proxy, client.CreateProxy<IOrdersContract>()),
            "the default CreateProxy alias must return the same local fail-fast proxy");
        Ensure(transport.Connection.SentRequestCount == 0,
            "local proxy creation must not serialize or send an RPC Request");
    }

    [Test]
    public async Task PreconnectedIncompatibleProxyShouldNotPreventSessionReadiness()
    {
        var transport = CreateTransport(new RpcHash128(3, 4));
        await using var client = CreateClient(transport);
        var proxy = client.Get<IOrdersContract>();

        await client.ConnectAsync();

        Ensure(proxy is OrdersProxy && client.State == SharpLinkConnectionState.Ready,
            "a local proxy must not turn unrelated session readiness into a compatibility check");
        Ensure(transport.Connection.SentRequestCount == 0,
            "connecting with a pre-acquired proxy must not send a business request");
    }

    [Test]
    public async Task MissingLocalDescriptorShouldRejectGetSynchronously()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        Exception? failure = null;
        try
        {
            _ = client.Get<IUnroutedContract>();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException, "Get must synchronously reject a missing local descriptor");
        Ensure(transport.ConnectCount == 0 && transport.Connection.SentRequestCount == 0,
            "missing local descriptors must not trigger remote work");
    }

    [Test]
    public async Task HandshakeContractManifestShouldHonorNegotiatedFrameLimit()
    {
        var transport = new OversizedManifestHandshakeTransportFactory();
        await using var client = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([Manifest.Instance]))
            .DisableRequestTimeout()
            .UseProtocol(static options => options.MaxFramePayloadBytes = 4096)
            .UseTransport(transport)
            .Build();

        var failure = await CaptureExceptionAsync(client.ConnectAsync().AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation },
            "a ContractManifest above the negotiated frame maximum must fail the client handshake");
        Ensure(failure is not null &&
               failure.Message.Contains("negotiated maximum of 2048 bytes", StringComparison.Ordinal),
            "the handshake failure must identify the negotiated frame boundary");
        Ensure(client.State != SharpLinkConnectionState.Ready,
            "an oversized handshake ContractManifest must not publish a Ready client");
    }

    private static TestClientTransportFactory CreateTransport(RpcHash128 remoteHash)
        => new(
            contractManifest:
            [
                new KeyValuePair<long, RpcHash128>(OrdersContractId, remoteHash)
            ]);

    private static ISharpLinkClient CreateClient(TestClientTransportFactory transport)
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([Manifest.Instance]))
            .DisableRequestTimeout()
            .UseTransport(transport)
            .Build();

    private sealed class OversizedManifestHandshakeTransportFactory : IClientTransportFactory
    {
        private readonly TestTransportConnection _connection = new();

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            using var responsePayload = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(
                responsePayload,
                new ProtocolV2HandshakeResponse(
                    ProtocolV2Constants.MinorVersion,
                    ProtocolV2Capabilities.ContractManifest | ProtocolV2Capabilities.ContractAdmission,
                    2048,
                    1024 * 1024,
                    16 * 1024 * 1024));
            await _connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0,
                responsePayload.WrittenMemory,
                cancellationToken);

            await _connection.InjectFrameAsync(
                ProtocolV2FrameType.ContractManifest,
                ProtocolV2FrameFlags.None,
                0,
                new byte[3072],
                cancellationToken);
            return _connection;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
