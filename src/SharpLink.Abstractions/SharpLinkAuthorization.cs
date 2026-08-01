namespace SharpLink.Abstractions;

/// <summary>Provides server-side authorization guards for the current RPC call.</summary>
public static class SharpLinkAuthorization
{
    /// <summary>Gets the current call's authentication context or rejects the call.</summary>
    /// <param name="message">An optional peer-facing rejection message.</param>
    /// <returns>The established authentication context.</returns>
    /// <exception cref="SharpLinkException">The call has no authentication context.</exception>
    public static SharpLinkAuthenticationContext GetRequiredAuthentication(string? message = null)
    {
        var authentication = SharpLinkCallContext.Current?.Authentication;
        if (authentication is not null)
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthenticationRejected,
            string.IsNullOrWhiteSpace(message) ? "Authentication context is required." : message);
    }

    /// <summary>Requires a current authentication context whose credential has not expired.</summary>
    /// <param name="now">The comparison instant, or <see langword="null"/> to use UTC now.</param>
    /// <param name="message">An optional peer-facing rejection message.</param>
    /// <returns>The active authentication context.</returns>
    /// <exception cref="SharpLinkException">Authentication is absent or expired.</exception>
    public static SharpLinkAuthenticationContext RequireActiveToken(DateTimeOffset? now = null, string? message = null)
    {
        var authentication = GetRequiredAuthentication(message);
        if (!authentication.IsExpired(now))
            return authentication;

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthenticationExpired,
            string.IsNullOrWhiteSpace(message) ? "Authentication token has expired." : message);
    }

    /// <summary>Requires the current identity to contain a case-sensitive scope.</summary>
    /// <param name="scope">The required non-empty scope.</param>
    /// <param name="message">An optional peer-facing rejection message.</param>
    /// <returns>The authorized authentication context.</returns>
    /// <exception cref="SharpLinkException">Authentication is absent or the scope is missing.</exception>
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

    /// <summary>Requires the current identity to match a tenant using ordinal comparison.</summary>
    /// <param name="tenantId">The required non-empty tenant identifier.</param>
    /// <param name="message">An optional peer-facing rejection message.</param>
    /// <returns>The authorized authentication context.</returns>
    /// <exception cref="SharpLinkException">Authentication is absent or the tenant does not match.</exception>
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
