using System.Collections.ObjectModel;

namespace SharpLink.Abstractions;

public sealed class SharpLinkAuthenticationContext
{
    private static readonly IReadOnlyDictionary<string, string> SEmptyClaims =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    private static readonly IReadOnlySet<string> SEmptyScopes = new HashSet<string>(StringComparer.Ordinal);

    public string? Subject { get; }
    public string? TenantId { get; }
    public IReadOnlySet<string> Scopes { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public IReadOnlyDictionary<string, string> Claims { get; }

    public SharpLinkAuthenticationContext(
        string? subject = null,
        string? tenantId = null,
        IEnumerable<string>? scopes = null,
        DateTimeOffset? expiresAt = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        Subject = subject;
        TenantId = tenantId;
        ExpiresAt = expiresAt;

        if (scopes is null)
        {
            Scopes = SEmptyScopes;
        }
        else
        {
            var normalizedScopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scope in scopes)
            {
                if (string.IsNullOrWhiteSpace(scope))
                    continue;

                normalizedScopes.Add(scope);
            }

            Scopes = normalizedScopes.Count == 0 ? SEmptyScopes : normalizedScopes;
        }

        if (claims is null || claims.Count == 0)
        {
            Claims = SEmptyClaims;
            return;
        }

        Claims = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(claims, StringComparer.Ordinal));
    }

    public string? GetClaim(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Claims.TryGetValue(name, out var value) ? value : null;
    }

    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(scope);
    }

    public bool IsExpired(DateTimeOffset? now = null)
    {
        if (ExpiresAt is not { } expiresAt)
            return false;

        return expiresAt <= (now ?? DateTimeOffset.UtcNow);
    }
}
