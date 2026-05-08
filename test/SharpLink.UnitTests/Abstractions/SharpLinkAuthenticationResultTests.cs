namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkAuthenticationResultTests
{
    [Test]
    public void RejectShouldRoundTripPayloadMessage()
    {
        var expected = SharpLinkAuthenticationResult.Reject(
            SharpLinkErrorCode.AuthenticationExpired,
            "token expired");

        var payload = expected.ToPayloadMessage();
        Ensure(SharpLinkAuthenticationResult.TryParsePayloadMessage(payload, out var actual), "payload should parse");
        Ensure(actual == expected, "parsed payload should match original");
    }

    [Test]
    public void TryParsePayloadMessageShouldReturnFalseForLegacyMessage()
    {
        Ensure(
            !SharpLinkAuthenticationResult.TryParsePayloadMessage("Authentication rejected.", out _),
            "legacy message should not parse as structured payload");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
