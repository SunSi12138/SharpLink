using System.Collections.ObjectModel;
using System.Collections.Frozen;

namespace SharpLink.Abstractions;

/// <summary>Contains the normalized identity established for one authenticated connection.</summary>
public sealed class SharpLinkAuthenticationContext
{
    private static readonly IReadOnlyDictionary<string, string> SEmptyClaims =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    private static readonly IReadOnlySet<string> SEmptyScopes = FrozenSet<string>.Empty;

    /// <summary>Gets the authenticated subject identifier, when supplied.</summary>
    public string? Subject { get; }
    /// <summary>Gets the tenant identifier used for tenant-aware authorization.</summary>
    public string? TenantId { get; }
    /// <summary>Gets the case-sensitive authorization scopes.</summary>
    public IReadOnlySet<string> Scopes { get; }
    /// <summary>Gets the credential expiration instant, when the credential expires.</summary>
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>Gets additional case-sensitive identity claims.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>Creates an immutable authentication context.</summary>
    /// <param name="subject">The authenticated subject identifier.</param>
    /// <param name="tenantId">The authenticated tenant identifier.</param>
    /// <param name="scopes">Authorization scopes; blank entries are ignored and duplicates are removed.</param>
    /// <param name="expiresAt">The credential expiration instant.</param>
    /// <param name="claims">Additional identity claims copied with ordinal key comparison.</param>
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

            Scopes = normalizedScopes.Count == 0
                ? SEmptyScopes
                : normalizedScopes.ToFrozenSet(StringComparer.Ordinal);
        }

        if (claims is null || claims.Count == 0)
        {
            Claims = SEmptyClaims;
            return;
        }

        Claims = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(claims, StringComparer.Ordinal));
    }

    /// <summary>Gets a claim using case-sensitive ordinal matching.</summary>
    /// <param name="name">The non-empty claim name.</param>
    /// <returns>The claim value, or <see langword="null"/> when absent.</returns>
    public string? GetClaim(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Claims.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Determines whether the identity has a scope using case-sensitive ordinal matching.</summary>
    /// <param name="scope">The non-empty scope name.</param>
    /// <returns><see langword="true"/> when the scope is present.</returns>
    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(scope);
    }

    /// <summary>Determines whether the credential has expired.</summary>
    /// <param name="now">The comparison instant, or <see langword="null"/> to use UTC now.</param>
    /// <returns><see langword="true"/> when an expiration exists and is not later than the comparison instant.</returns>
    public bool IsExpired(DateTimeOffset? now = null)
    {
        if (ExpiresAt is not { } expiresAt)
            return false;

        return expiresAt <= (now ?? DateTimeOffset.UtcNow);
    }
}
