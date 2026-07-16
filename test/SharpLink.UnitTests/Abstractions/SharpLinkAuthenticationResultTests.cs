namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkAuthenticationResultTests
{
    [Test]
    public void RejectShouldPreserveStructuredError()
    {
        var result = SharpLinkAuthenticationResult.Reject(
            SharpLinkErrorCode.AuthenticationExpired,
            "token expired");

        Ensure(!result.IsAuthenticated, "rejection should not authenticate");
        Ensure(result.ErrorCode == SharpLinkErrorCode.AuthenticationExpired, "error code");
        Ensure(result.ErrorMessage == "token expired", "error message");
    }

    [Test]
    public void RejectShouldUseSafeDefaultMessage()
    {
        var result = SharpLinkAuthenticationResult.Reject(SharpLinkErrorCode.AuthorizationDenied);
        Ensure(result.ErrorMessage == "Authorization denied.", "default message");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
