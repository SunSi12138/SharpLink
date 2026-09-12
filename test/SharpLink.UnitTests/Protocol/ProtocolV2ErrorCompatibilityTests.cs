namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2ErrorCompatibilityTests
{
    [Test]
    public async Task StructuredErrorShapeShouldRejectPublishedMinorThreePeers()
    {
        Ensure(ProtocolV2Constants.MinorVersion == 4, "2.0 increments the published minor 3 once");
        Ensure(
            ProtocolV2Constants.MinimumCompatibleMinorVersion == 4,
            "published minor-3 peers must be rejected before decoding the structured error shape");

        var policy = ProtocolV2Negotiator.CreateImplementedPolicy(
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
        var legacyMinor = checked((ushort)(ProtocolV2Constants.MinimumCompatibleMinorVersion - 1));
        var legacyOffer = new ProtocolV2HandshakeRequest(
            legacyMinor,
            ProtocolV2Capabilities.None,
            ProtocolV2Capabilities.None,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            ReadOnlyMemory<byte>.Empty);

        var serverFailure = Capture(() => ProtocolV2Negotiator.NegotiateServer(legacyOffer, policy));
        await Assert.That(serverFailure).IsAssignableTo<SharpLinkException>();
        await Assert.That((serverFailure as SharpLinkException)?.Code)
            .IsEqualTo(SharpLinkErrorCode.Unimplemented);

        var currentOffer = ProtocolV2Negotiator.CreateClientOffer(
            policy,
            ProtocolV2Capabilities.None,
            ReadOnlyMemory<byte>.Empty);
        var legacyResponse = new ProtocolV2HandshakeResponse(
            legacyMinor,
            ProtocolV2Capabilities.None,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024);

        var clientFailure = Capture(() =>
            ProtocolV2Negotiator.ValidateServerResponse(currentOffer, legacyResponse, policy));
        await Assert.That(clientFailure).IsAssignableTo<SharpLinkException>();
        await Assert.That((clientFailure as SharpLinkException)?.Code)
            .IsEqualTo(SharpLinkErrorCode.Unimplemented);
    }

    private static Exception? Capture(Action action)
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
