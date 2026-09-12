namespace SharpLink.Hosting;

/// <summary>Provides the running multi-cluster coordinator managed by the generic host.</summary>
public interface ISharpLinkMultiClusterClientAccessor
{
    /// <summary>
    /// Gets the published coordinator or waits for its local runtime to start; individual clusters may still be unavailable.
    /// </summary>
    ValueTask<ISharpLinkMultiClusterClient> GetClientAsync(CancellationToken cancellationToken = default);
}
