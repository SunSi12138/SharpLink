namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2TimeBudgetCompatibilityTests
{
    [Test]
    public void PreTimeBudgetProtocolMinorShouldBeRejectedAtNegotiation()
    {
        var serverPolicy = ProtocolV2Negotiator.CreateImplementedPolicy(
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var legacyOffer = new ProtocolV2HandshakeRequest(
            MinorVersion: 3,
            SupportedCapabilities: ProtocolV2Capabilities.None,
            RequiredCapabilities: ProtocolV2Capabilities.None,
            MaxFramePayloadBytes: 4 * 1024 * 1024,
            StreamReceiveWindowBytes: 1024 * 1024,
            ConnectionReceiveWindowBytes: 16 * 1024 * 1024,
            AuthenticationPayload: ReadOnlyMemory<byte>.Empty);

        var failure = CaptureException(() => ProtocolV2Negotiator.NegotiateServer(legacyOffer, serverPolicy));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented },
            "minor 3 must be rejected instead of negotiating an absolute-deadline wire shape");
    }

    [Test]
    public void ClientShouldRejectServerResponseBelowTimeBudgetBoundary()
    {
        var clientPolicy = ProtocolV2Negotiator.CreateImplementedPolicy(
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var offer = ProtocolV2Negotiator.CreateClientOffer(
            clientPolicy,
            ProtocolV2Capabilities.None,
            ReadOnlyMemory<byte>.Empty);
        var legacyResponse = new ProtocolV2HandshakeResponse(
            MinorVersion: 3,
            NegotiatedCapabilities: ProtocolV2Capabilities.None,
            MaxFramePayloadBytes: 4 * 1024 * 1024,
            StreamReceiveWindowBytes: 1024 * 1024,
            ConnectionReceiveWindowBytes: 16 * 1024 * 1024);

        var failure = CaptureException(() =>
            ProtocolV2Negotiator.ValidateServerResponse(offer, legacyResponse, clientPolicy));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unimplemented },
            "client must reject a server that selects the legacy absolute-deadline minor");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
