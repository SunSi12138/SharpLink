using System.Collections.ObjectModel;

namespace SharpLink.Abstractions;

/// <summary>Represents one versioned endpoint topology supplied by a resolver.</summary>
/// <remarks>
/// The client copies and validates <see cref="Endpoints"/> before accepting the snapshot. Versions are
/// compared by the client and must increase strictly for a topology update to take effect.
/// </remarks>
public sealed class SharpLinkEndpointSnapshot
{
    private readonly ReadOnlyCollection<SharpLinkEndpoint> _endpoints;

    /// <summary>Initializes a versioned endpoint snapshot.</summary>
    /// <param name="version">A resolver-assigned, non-negative topology version.</param>
    /// <param name="endpoints">The endpoints in this complete topology snapshot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    public SharpLinkEndpointSnapshot(long version, IReadOnlyList<SharpLinkEndpoint> endpoints)
    {
        if (version < 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(endpoints);

        Version = version;
        var snapshot = new SharpLinkEndpoint[endpoints.Count];
        for (var index = 0; index < endpoints.Count; index++)
            snapshot[index] = endpoints[index] ?? throw new ArgumentException(
                "Endpoint snapshots cannot contain null endpoints.", nameof(endpoints));
        _endpoints = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the resolver-assigned version for this complete topology.</summary>
    public long Version { get; }

    /// <summary>Gets the endpoint collection supplied by the resolver.</summary>
    public IReadOnlyList<SharpLinkEndpoint> Endpoints => _endpoints;
}

/// <summary>Supplies complete endpoint topology snapshots to a dynamic SharpLink client.</summary>
/// <remarks>
/// Resolvers must not create or retain client transport factories. A client owns the resolver passed to
/// its builder and disposes it exactly once when the client stops or is disposed.
/// </remarks>
public interface ISharpLinkEndpointResolver : IAsyncDisposable
{
    /// <summary>Resolves the initial complete topology.</summary>
    /// <param name="cancellationToken">Cancels the resolution attempt.</param>
    /// <returns>The latest complete topology snapshot.</returns>
    ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>Watches for complete topology snapshots after the initial resolution.</summary>
    /// <param name="cancellationToken">Stops the watch when the owning client stops.</param>
    /// <returns>An asynchronous sequence of complete topology snapshots.</returns>
    IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(CancellationToken cancellationToken);
}
