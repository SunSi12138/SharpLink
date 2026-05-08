namespace SharpLink.Abstractions;

public static class SharpLinkAuthorization
{
    public static SharpLinkAuthenticationContext GetRequiredAuthentication(string? message = null)
    {
        var authentication = SharpLinkCallContext.Current?.Authentication;
        if (authentication is not null)
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthenticationRejected,
            string.IsNullOrWhiteSpace(message) ? "Authentication context is required." : message);
    }

    public static SharpLinkAuthenticationContext RequireActiveToken(DateTimeOffset? now = null, string? message = null)
    {
        var authentication = GetRequiredAuthentication(message);
        if (!authentication.IsExpired(now))
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthenticationExpired,
            string.IsNullOrWhiteSpace(message) ? "Authentication token has expired." : message);
    }

    public static SharpLinkAuthenticationContext RequireScope(string scope, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var authentication = GetRequiredAuthentication(message);
        if (authentication.HasScope(scope))
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthorizationDenied,
            string.IsNullOrWhiteSpace(message) ? $"Required scope '{scope}' is missing." : message);
    }

    public static SharpLinkAuthenticationContext RequireTenant(string tenantId, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var authentication = GetRequiredAuthentication(message);
        if (string.Equals(authentication.TenantId, tenantId, StringComparison.Ordinal))
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthorizationDenied,
            string.IsNullOrWhiteSpace(message) ? $"Required tenant '{tenantId}' does not match." : message);
    }
}
