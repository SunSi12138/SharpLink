namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2WireGenerationBoundaryTests
{
    [Test]
    public void PreviousMinorShouldBeRejectedDuringHandshake()
    {
        Ensure(
            ProtocolV2Constants.MinimumCompatibleMinorVersion == ProtocolV2Constants.MinorVersion,
            "the intentional DTO wire break must advance the current minor and compatibility floor together");
        var previousMinor = checked((ushort)(ProtocolV2Constants.MinimumCompatibleMinorVersion - 1));
        var policy = ProtocolV2Negotiator.CreateImplementedPolicy(
            SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
            1024,
            2048,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var offer = ProtocolV2Negotiator.CreateClientOffer(
            policy,
            ProtocolV2Capabilities.None,
            ReadOnlyMemory<byte>.Empty);

        var serverFailure = Capture(() => ProtocolV2Negotiator.NegotiateServer(
            offer with { MinorVersion = previousMinor },
            policy));
        Ensure(
            serverFailure.Code == SharpLinkErrorCode.Unimplemented,
            "the server must reject a previous wire-generation offer during handshake");

        var response = new ProtocolV2HandshakeResponse(
            offer.MinorVersion,
            ProtocolV2Capabilities.None,
            offer.MaxFramePayloadBytes,
            offer.StreamReceiveWindowBytes,
            offer.ConnectionReceiveWindowBytes);
        var clientFailure = Capture(() => ProtocolV2Negotiator.ValidateServerResponse(
            offer,
            response with { MinorVersion = previousMinor },
            policy));
        Ensure(
            clientFailure.Code == SharpLinkErrorCode.Unimplemented,
            "the client must reject a previous wire-generation response during handshake");
    }

    private static SharpLinkException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected SharpLinkException.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
