using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractManifestTests : SharpLinkMultiClusterClientTestBase
{
    private const long OrdersContractId = 8_101;

    [Test]
    public async Task EqualRpcAssemblyHashAllowsReadinessObservationWithoutBinding()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);

        await client.ConnectAsync();
        var observation = client.WaitForRemoteContractAsync(OrdersContractId, Manifest.Instance.RpcAssemblyHash);
        Ensure(observation.IsCompletedSuccessfully,
            "an exact hash on the Ready transport must be visible without a new connection");
        await observation;
        var proxy = client.CreateProxy<IOrdersContract>();
        Ensure(proxy.Service is OrdersProxy && transport.ConnectCount == 1,
            "local proxy creation must preserve the original connection");
        await EnsureNoBindOrRequestAsync(transport.Connection);
    }

    [Test]
    public async Task MismatchedRpcAssemblyHashShouldRejectPrepareBeforeAnyBindOrRpcPayload()
    {
        var remoteHash = new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var proxy = client.CreateProxy<IOrdersContract>();
        var failure = await CaptureExceptionAsync(proxy.PrepareAsync(TimeSpan.FromSeconds(2)).AsTask());

        if (failure is not SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition } exception)
            throw new Exception("A mismatched RpcAssemblyHash must fail preparation with FailedPrecondition.", failure);
        Ensure(exception.Message.Contains(typeof(IOrdersContract).FullName!, StringComparison.Ordinal) &&
               exception.Message.Contains(OrdersContractId.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(Manifest.Instance.RpcAssemblyHash.ToString(), StringComparison.Ordinal) &&
               exception.Message.Contains(remoteHash.ToString(), StringComparison.Ordinal),
            "the mismatch diagnostic must identify the contract and both exact hashes");
        await EnsureNoBindOrRequestAsync(transport.Connection);
    }

    [Test]
    public async Task PreconnectedProxyStaysLocalAndIncompatibilityFailsOnlyAtPrepare()
    {
        var remoteHash = new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        var proxy = client.CreateProxy<IOrdersContract>();
        var service = proxy.Service;
        Ensure(service is OrdersProxy && transport.ConnectCount == 0 &&
               client.State == SharpLinkConnectionState.Created,
            "creating a typed proxy before ConnectAsync is strictly local");

        await client.ConnectAsync();
        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1 &&
               ReferenceEquals(proxy.Service, service),
            "a local proxy does not make a mismatched contract a transport handshake requirement");
        var failure = await CaptureExceptionAsync(proxy.PrepareAsync(TimeSpan.FromSeconds(2)).AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "the initial remote manifest must reject preparation of the mismatched contract");
        Ensure(client.State == SharpLinkConnectionState.Ready && transport.ConnectCount == 1,
            "contract preparation failure must not retire the otherwise Ready connection");
        await EnsureNoBindOrRequestAsync(transport.Connection);
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
    public async Task ManifestRefreshChangesPreparationWithoutReplacingTheLocalHandle()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();
        var heldProxy = client.CreateProxy<IOrdersContract>();
        var service = heldProxy.Service;
        var replacementHash = new RpcHash128(0x4142434445464748UL, 0x5152535455565758UL);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var refreshed = client.WaitForRemoteContractAsync(OrdersContractId, replacementHash,
            Manifest.Instance.RpcAssemblyHash, timeout.Token).AsTask();
        Ensure(!refreshed.IsCompleted, "the observed previous hash must not satisfy the new observation");

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
            payload.WrittenMemory, timeout.Token);
        await refreshed;

        Ensure(ReferenceEquals(service, heldProxy.Service) &&
               client.CreateProxy<IOrdersContract>().Service is OrdersProxy,
            "manifest refresh does not replace the held service or forbid a new local handle");
        var mismatch = await CaptureExceptionAsync(heldProxy.PrepareAsync(TimeSpan.FromSeconds(2)).AsTask());
        Ensure(mismatch is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition } &&
               mismatch.Message.Contains(replacementHash.ToString(), StringComparison.Ordinal),
            "preparation must reject the latest incompatible remote manifest, even for an older local handle");
        await EnsureNoBindOrRequestAsync(transport.Connection);
    }

    [Test]
    public void SingleClientExposesOnlyPreparedProxyAndContractFactories()
    {
        Ensure(typeof(ISharpLinkClient).GetMethod("Get") is null &&
               typeof(SharpLinkClient).GetMethod("Get") is null &&
               typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateProxy)) is not null &&
               typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateContractChannel)) is not null,
            "the client factory surface must not contain the legacy generated proxy path");
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

    private static async Task EnsureNoBindOrRequestAsync(TestTransportConnection connection)
    {
        while (await connection.TryReadNextSentFrameAsync(TimeSpan.FromMilliseconds(50)) is { } frame)
        {
            Ensure(frame.Header.Type != NStudyWire.Bind && !NStudyWire.IsRequestFrame(frame.Header.Type),
                "observing readiness or rejecting a known mismatch must not write Bind or any RPC Request");
        }
    }

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
