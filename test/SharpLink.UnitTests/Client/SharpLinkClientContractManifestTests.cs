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
    public async Task MismatchedRpcAssemblyHashShouldStillAllowLocalProxyCreation()
    {
        var transport = CreateTransport(new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL));
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var proxy = client.CreateProxy<IOrdersContract>();
        Ensure(proxy is OrdersProxy && ReferenceEquals(proxy, client.Get<IOrdersContract>()),
            "remote incompatibility cannot reject or replace a valid local proxy");
        var failure = await CaptureExceptionAsync(InvokeOneWay((OrdersProxy)proxy).AsTask());
        Ensure(failure is SharpLinkException
            { Code: SharpLinkErrorCode.Unavailable, DetailCode: SharpLinkErrorDetails.Unavailable.NoRoutableTarget },
            "the ordinary call must report current routing unavailability before sending payload");
    }

    [Test]
    public async Task PreconnectedProxyShouldNotPreventIncompatibleSessionReadiness()
    {
        var transport = CreateTransport(new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL));
        await using var client = CreateClient(transport);
        var preconnected = client.CreateProxy<IOrdersContract>();
        Ensure(transport.ConnectCount == 0, "creating a proxy must not connect");

        await client.ConnectAsync();

        Ensure(ReferenceEquals(preconnected, client.Get<IOrdersContract>()),
            "a proxy freezes local identity independently of later handshakes");
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "an incompatible contract does not make an otherwise healthy session unusable for other contracts");
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
    public async Task ManifestRefreshShouldChangeRoutesWithoutRebindingHeldProxy()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();
        var heldProxy = client.Get<IOrdersContract>();
        var replacementHash = new RpcHash128(0x4142434445464748UL, 0x5152535455565758UL);

        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(payload,
            new ProtocolV2ContractManifest(1, [new KeyValuePair<long, RpcHash128>(OrdersContractId, replacementHash)]),
            new SharpLinkProtocolOptions());
        await transport.Connection.InjectFrameAsync(ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None, 0, payload.WrittenMemory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await client.WaitForRemoteContractAsync(OrdersContractId, replacementHash,
            Manifest.Instance.RpcAssemblyHash, deadline.Token);

        Ensure(ReferenceEquals(heldProxy, client.CreateProxy<IOrdersContract>()),
            "manifest publication changes the picker, never the local proxy or its codec");
        var failure = await CaptureExceptionAsync(InvokeOneWay((OrdersProxy)heldProxy).AsTask());
        Ensure(failure is SharpLinkException
            { Code: SharpLinkErrorCode.Unavailable, DetailCode: SharpLinkErrorDetails.Unavailable.NoRoutableTarget },
            "the held proxy must consult the current route on its next invocation");
    }

    private static ValueTask InvokeOneWay(OrdersProxy proxy)
    {
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        var method = new RpcMethodDescriptor(OrdersContractId, 1, RpcMethodKind.OneWay, false, false, false, null);
        return proxy.Channel.InvokeOneWayAsync(method, request, RpcEmptyRequestCodec.Instance, streams, null);
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
                    16 * 1024 * 1024,
                    ServerInstanceId: ClientInvocationTestManifest.ServerInstanceId,
                    RegistryEpoch: ClientInvocationTestManifest.RegistryEpoch));
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
