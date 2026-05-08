namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkAuthorizationTests
{
    [Test]
    public void RequireScopeShouldReturnAuthenticationWhenScopeExists()
    {
        var authentication = new SharpLinkAuthenticationContext(
            subject: "user-42",
            tenantId: "tenant-a",
            scopes: ["rpc.read"]);

        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot("session-1", authentication));
        var resolved = SharpLinkAuthorization.RequireScope("rpc.read");

        Ensure(ReferenceEquals(authentication, resolved), "scope guard should return original authentication context");
    }

    [Test]
    public void RequireTenantShouldThrowAuthorizationDeniedWhenTenantDiffers()
    {
        var authentication = new SharpLinkAuthenticationContext(tenantId: "tenant-a");
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot("session-1", authentication));

        try
        {
            SharpLinkAuthorization.RequireTenant("tenant-b");
            throw new Exception("expected RequireTenant to throw");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == SharpLinkErrorCode.AuthorizationDenied, "tenant guard error code");
        }
    }

    [Test]
    public void RequireActiveTokenShouldThrowAuthenticationExpiredWhenExpired()
    {
        var authentication = new SharpLinkAuthenticationContext(
            expiresAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot("session-1", authentication));

        try
        {
            SharpLinkAuthorization.RequireActiveToken(new DateTimeOffset(2026, 4, 19, 12, 0, 1, TimeSpan.Zero));
            throw new Exception("expected RequireActiveToken to throw");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == SharpLinkErrorCode.AuthenticationExpired, "expiry guard error code");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
