namespace SharpLink.Hosting;

/// <summary>Provides the hosted multi-cluster client only after every required slot is ready.</summary>
public interface ISharpLinkMultiClusterClientAccessor
{
    /// <summary>Gets the published coordinator or waits for hosted startup to finish.</summary>
    ValueTask<ISharpLinkMultiClusterClient> GetClientAsync(CancellationToken cancellationToken = default);
}
