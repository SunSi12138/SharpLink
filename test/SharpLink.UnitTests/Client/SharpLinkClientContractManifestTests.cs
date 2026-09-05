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
    public async Task MismatchedRpcAssemblyHashShouldRejectGetBeforeAnyRpcPayload()
    {
        var remoteHash = new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();

        var failure = CaptureGetFailure(client);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "a mismatched RpcAssemblyHash must fail contract acquisition with FailedPrecondition");
        var exception = (SharpLinkException)failure;
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
    public async Task PreconnectedProxyShouldBeValidatedBeforeSessionBecomesCallable()
    {
        var remoteHash = new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL);
        var transport = CreateTransport(remoteHash);
        await using var client = CreateClient(transport);
        var preconnected = client.Get<IOrdersContract>();

        var failure = await CaptureExceptionAsync(client.ConnectAsync().AsTask());

        Ensure(preconnected is OrdersProxy,
            "Get<T>() must retain the historical ability to create a proxy before ConnectAsync");
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "the initial remote manifest must reject an already-acquired incompatible proxy before readiness publication");
        Ensure(client.State != SharpLinkConnectionState.Ready,
            "an incompatible pre-acquired proxy must prevent the connection from becoming Ready");
        Ensure(!await transport.Connection.TryWaitForSentPacket(
                ProtocolV2FrameType.Request,
                TimeSpan.FromMilliseconds(50)),
            "pre-connect compatibility rejection must occur before any RPC Request payload");
    }

    [Test]
    public async Task ManifestRefreshShouldRevalidateFutureGetWithoutRebindingHeldProxy()
    {
        var transport = CreateTransport(Manifest.Instance.RpcAssemblyHash);
        await using var client = CreateClient(transport);
        await client.ConnectAsync();
        var heldProxy = client.Get<IOrdersContract>();
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

        SharpLinkException? mismatch = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                _ = client.Get<IOrdersContract>();
            }
            catch (SharpLinkException exception)
            {
                mismatch = exception;
                break;
            }
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        Ensure(heldProxy is OrdersProxy,
            "a manifest refresh must not replace a proxy reference already returned to user code");
        Ensure(mismatch is { Code: SharpLinkErrorCode.FailedPrecondition } &&
               mismatch.Message.Contains(replacementHash.ToString(), StringComparison.Ordinal),
            "future Get<T>() must validate against the latest remote manifest generation");
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

    private static Exception CaptureGetFailure(ISharpLinkClient client)
    {
        try
        {
            _ = client.Get<IOrdersContract>();
            return new Exception("expected RpcAssemblyHash mismatch");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
