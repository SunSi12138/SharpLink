namespace SharpLink.Hosting;

/// <summary>Provides the client managed by the generic-host connectivity lifecycle service.</summary>
public interface ISharpLinkClientAccessor
{
    /// <summary>Waits until the hosted client is available.</summary>
    /// <param name="cancellationToken">Cancels only this wait.</param>
    /// <returns>The hosted client after its topology-specific connectivity boundary completes.</returns>
    ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default);
}
