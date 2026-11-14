using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractManifestTests : SharpLinkMultiClusterClientTestBase
{
    private const long OrdersContractId = 8_101;

    [Test]
    public async Task EqualRpcAssemblyHashShouldAllowAcknowledgedPreparation()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);

        await client.ConnectAsync();
        var proxy = client.CreateProxy<IOrdersContract>();
        await proxy.PrepareAsync(RaceCoordinationTimeout);

        Ensure(proxy.Service is OrdersProxy,
            "an exactly matching remote RpcAssemblyHash must permit acknowledged binding preparation");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "contract acquisition itself must not emit an RPC Request frame");
    }

    [Test]
    public async Task MismatchedRpcAssemblyHashShouldRejectPrepareBeforeAnyRpcPayload()
    {
        var remoteHash = new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var proxy = client.CreateProxy<IOrdersContract>();
        Ensure(proxy.Service is OrdersProxy, "local construction must not query remote compatibility");
        var failure = await CaptureExceptionAsync(proxy.PrepareAsync(RaceCoordinationTimeout).AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "a mismatched RpcAssemblyHash must fail contract acquisition with FailedPrecondition");
        var exception = (SharpLinkException)failure!;
        Ensure(exception.Message.Contains(typeof(IOrdersContract).FullName!, StringComparison.Ordinal) &&
               exception.Message.Contains(OrdersContractId.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(Manifest.Instance.RpcAssemblyHash.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(remoteHash.ToString(), StringComparison.Ordinal),
            "the mismatch diagnostic must identify the contract and both exact hashes");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "a bind mismatch must be rejected before any RPC Request payload is emitted");
    }

    [Test]
    public async Task PreconnectedLocalProxyShouldFailPrepareWithoutPoisoningSessionReadiness()
    {
        var remoteHash = new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        var preconnected = client.CreateProxy<IOrdersContract>();
        Ensure(preconnected.Service is OrdersProxy && transport.ConnectCount == 0,
            "the local factory must remain independent of Connect and remote identity");
        await client.ConnectAsync();
        var failure = await CaptureExceptionAsync(preconnected.PrepareAsync(RaceCoordinationTimeout).AsTask());

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "an incompatible site must fail its finite Prepare before any business payload");
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "a contract binding failure must not poison transport/session readiness");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "pre-connect compatibility rejection must occur before any RPC Request payload");
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

    [Test]
    public async Task ManifestRefreshShouldPruneBindingWithoutReplacingHeldLocalService()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();
        var heldProxy = client.CreateProxy<IOrdersContract>();
        var heldService = heldProxy.Service;
        await heldProxy.PrepareAsync(RaceCoordinationTimeout);
        var replacementHash = new RpcHash128(0x4142434445464748UL, 0x5152535455565758UL);

        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(
            payload,
            new ProtocolV2ContractManifest(
                1,
                [new KeyValuePair<long, RpcHash128>(OrdersContractId, replacementHash)]),
            new SharpLinkProtocolOptions());
        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None,
            0,
            payload.WrittenMemory);

        // An ordered Pong fences the manifest publication without polling or retrying Prepare.
        await transport.Connection.InjectFrameAsync(ProtocolV2FrameType.Ping,
            ProtocolV2FrameFlags.None, 0, new byte[sizeof(long)]);
        await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Pong).WaitAsync(RaceCoordinationTimeout);
        var mismatch = await CaptureExceptionAsync(heldProxy.PrepareAsync(RaceCoordinationTimeout).AsTask());

        Ensure(heldService is OrdersProxy && ReferenceEquals(heldService, heldProxy.Service),
            "a manifest refresh must preserve the service reference retained by its original handle");
        Ensure(client.CreateProxy<IOrdersContract>().Service is OrdersProxy,
            "a later local construction must not be redefined as remote readiness validation");
        Ensure(mismatch is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition } &&
               mismatch.Message.Contains(replacementHash.ToString(), StringComparison.Ordinal),
            "the exact original site's next Prepare must fail against the new incompatible remote hash");
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
                    ProtocolV2Capabilities.ContractManifest,
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
