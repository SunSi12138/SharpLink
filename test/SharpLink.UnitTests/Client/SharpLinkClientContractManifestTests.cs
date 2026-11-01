using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractManifestTests : SharpLinkMultiClusterClientTestBase
{
    private const long OrdersContractId = 8_101;

    [Test]
    public async Task EqualRpcAssemblyHashShouldBeObservableWithoutAnRpcPayload()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);

        await client.ConnectAsync();
        var proxy = client.CreateProxy<IOrdersContract>();
        await client.WaitForRemoteContractAsync(OrdersContractId, Manifest.Instance.RpcAssemblyHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout);

        Ensure(proxy.Service is OrdersProxy,
            "the selected local factory must create its service independently of the matching locator observation");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "contract acquisition itself must not emit an RPC Request frame");
    }

    [Test]
    public async Task MismatchedRpcAssemblyHashShouldRejectLocatorObservationBeforeAnyRpcPayload()
    {
        var remoteHash = new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var local = client.CreateProxy<IOrdersContract>();
        Ensure(local.Service is OrdersProxy, "a wrong remote locator must not invalidate the local factory");
        var failure = await CaptureLocatorFailureAsync(client);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "a mismatched RpcAssemblyHash must fail locator observation with FailedPrecondition");
        var exception = (SharpLinkException)failure!;
        Ensure(exception.Message.Contains(OrdersContractId.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(Manifest.Instance.RpcAssemblyHash.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(remoteHash.ToString(), StringComparison.Ordinal),
            "the locator mismatch diagnostic must identify the contract ID and both exact hashes");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "a locator mismatch must be reported without emitting any RPC Request payload");
    }

    [Test]
    public async Task PreconnectedLocalProxyShouldNotMakeLocatorCompatibilityATransportReadinessCondition()
    {
        var remoteHash = new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        var preconnected = client.CreateProxy<IOrdersContract>();

        await client.ConnectAsync();
        var failure = await CaptureLocatorFailureAsync(client);

        Ensure(preconnected.Service is OrdersProxy,
            "the same locally created handle must keep its service across transport startup");
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "the incompatible remote identity must fail explicit locator observation");
        Ensure(client.State == SharpLinkConnectionState.Ready,
            "local proxy construction and locator incompatibility must not redefine transport readiness");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "observing incompatible identity must not emit any RPC Request payload");
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
    public async Task ManifestRefreshShouldAffectFutureLocatorObservationsWithoutReplacingHeldService()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();
        var heldProxy = client.CreateProxy<IOrdersContract>();
        var heldService = heldProxy.Service;
        var replacementHash = new RpcHash128(0x4142434445464748UL, 0x5152535455565758UL);

        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(
            payload,
            new ProtocolV2ContractManifest(
                2,
                [new KeyValuePair<long, RpcHash128>(OrdersContractId, replacementHash)]),
            new SharpLinkProtocolOptions());
        await transport.Connection.InjectFrameAsync(
            ProtocolV2FrameType.ContractManifest,
            ProtocolV2FrameFlags.None,
            0,
            payload.WrittenMemory);

        SharpLinkException? mismatch = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await client.WaitForRemoteContractAsync(OrdersContractId, Manifest.Instance.RpcAssemblyHash, timeout.Token);
            }
            catch (SharpLinkException exception)
            {
                mismatch = exception;
                break;
            }
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        var future = client.CreateProxy<IOrdersContract>();
        Ensure(heldService is OrdersProxy && ReferenceEquals(heldService, heldProxy.Service) &&
               future.Service is OrdersProxy,
            "a locator refresh must neither replace the held service nor reject future local factory construction");
        Ensure(mismatch is { Code: SharpLinkErrorCode.FailedPrecondition } &&
               mismatch.Message.Contains(replacementHash.ToString(), StringComparison.Ordinal),
            "future locator observations must validate the latest remote manifest generation");
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

    private static Task<Exception?> CaptureLocatorFailureAsync(ISharpLinkClient client)
        => CaptureExceptionAsync(client.WaitForRemoteContractAsync(
                OrdersContractId, Manifest.Instance.RpcAssemblyHash)
            .AsTask().WaitAsync(RaceCoordinationTimeout));

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
                    ProtocolV2Negotiator.MandatoryCapabilities,
                    2048,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    null,
                    _connection.Authority));
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
