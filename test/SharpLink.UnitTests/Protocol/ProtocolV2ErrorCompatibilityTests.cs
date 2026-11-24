namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2ErrorCompatibilityTests
{
    [Test]
    [Arguments((ushort)3)]
    [Arguments((ushort)4)]
    public async Task PreparedProfileShouldRejectPreviouslyPublishedWireMinors(ushort legacyMinor)
    {
        // The historical 2.0 release established minor 4; N is a private, incompatible profile.
        const ushort preparedProfileMinor = 0x79A7;
        Ensure(ProtocolV2Constants.MinorVersion == preparedProfileMinor,
            "this test must run on the exact private prepared wire generation");
        Ensure(
            ProtocolV2Constants.MinimumCompatibleMinorVersion == preparedProfileMinor,
            "the profile must reject older wire grammars before structured error decoding");

        var policy = ProtocolV2NegotiationPolicy.Create(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.AlwaysImplementedCapabilities,
            4 * 1024 * 1024,
            1024 * 1024,
            16 * 1024 * 1024,
            Array.Empty<SharpLinkCompressionProviderBinding>());
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
