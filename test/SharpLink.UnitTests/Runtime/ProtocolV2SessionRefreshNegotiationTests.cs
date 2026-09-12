using SharpLink.UnitTests.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class ProtocolV2SessionRefreshNegotiationTests
{
    [Test]
    public async Task AnonymousPipeClientHandshakeOfferShouldOmitSessionRefresh()
    {
        await using var listener = new AnonymousPipeServerTransportListener(1);
        var offer = await listener.AllocateAsync();
        await using var peer = await listener.AcceptAsync();
        await using var client = ClientBuilderTestHelper.Build(
            new AnonymousPipeClientTransportFactory(offer.InHandle, offer.OutHandle));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connecting = client.ConnectAsync(cancellation.Token);
        try
        {
            var limits = new SharpLinkProtocolOptions();
            while (true)
            {
                var read = await peer.Input.ReadAsync(cancellation.Token);
                var buffer = read.Buffer;
                if (!ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out var payload))
                {
                    peer.Input.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                }
                Ensure(header.Type == ProtocolV2FrameType.HandshakeRequest, "client emits its handshake offer first");
                var request = ProtocolV2PayloadCodec.ReadHandshakeRequest(payload, limits);
                Ensure((request.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0 &&
                       (request.RequiredCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0,
                    "a one-shot anonymous-pipe client must neither offer nor require session refresh");
                Ensure((request.SupportedCapabilities & ProtocolV2Capabilities.ContractManifest) != 0,
                    "disabling refresh preserves the ordinary contract-manifest handshake");
                peer.Input.AdvanceTo(buffer.Start, buffer.End);
                break;
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await connecting; }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task ReplaceableClientHandshakeOfferShouldAdvertiseSessionRefresh()
    {
        var factory = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(factory);
        await client.ConnectAsync();
        var sent = await factory.Connection.WaitForSentFrame(ProtocolV2FrameType.HandshakeRequest)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var request = ProtocolV2PayloadCodec.ReadHandshakeRequest(
            new ReadOnlySequence<byte>(sent.Payload), new SharpLinkProtocolOptions());
        Ensure((request.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0,
            "a replaceable client transport continues to advertise session refresh on the wire");
    }

    [Test]
    public void ImplementedPolicyShouldAdvertiseSessionRefreshWhenEnabled()
    {
        var policy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
            64 * 1024,
            256 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>(),
            enableSessionRefresh: true);

        Ensure((policy.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) != 0,
            "enabled session refresh should be advertised");
    }

    [Test]
    public void ImplementedPolicyShouldOmitSessionRefreshWhenReplacementIsUnsupported()
    {
        var policy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
            64 * 1024,
            256 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>(),
            enableSessionRefresh: false);

        Ensure((policy.SupportedCapabilities & ProtocolV2Capabilities.SessionRefresh) == 0,
            "one-shot transports should safely degrade to future-only convergence");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
