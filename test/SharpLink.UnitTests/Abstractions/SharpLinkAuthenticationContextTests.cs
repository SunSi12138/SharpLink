using System.Collections.Generic;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkAuthenticationContextTests
{
    [Test]
    public void ConstructorShouldNormalizeScopesAndClaims()
    {
        var expiresAt = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var context = new SharpLinkAuthenticationContext(
            subject: "user-42",
            tenantId: "tenant-a",
            scopes: ["rpc.read", "", "rpc.write", "rpc.read"],
            expiresAt: expiresAt,
            claims: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "admin"
            });

        Ensure(context.Subject == "user-42", "subject");
        Ensure(context.TenantId == "tenant-a", "tenant");
        Ensure(context.ExpiresAt == expiresAt, "expiresAt");
        Ensure(context.Scopes.Count == 2, "scope count");
        Ensure(context.HasScope("rpc.read"), "read scope");
        Ensure(context.HasScope("rpc.write"), "write scope");
        Ensure(context.GetClaim("role") == "admin", "role claim");
    }

    [Test]
    public void HasScopeShouldRejectBlankInput()
    {
        var context = new SharpLinkAuthenticationContext();

        try
        {
            context.HasScope(" ");
            throw new Exception("expected HasScope to reject blank input");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
