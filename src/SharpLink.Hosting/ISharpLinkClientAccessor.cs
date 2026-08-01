namespace SharpLink.Hosting;

/// <summary>Provides the connected client managed by the generic-host lifecycle service.</summary>
public interface ISharpLinkClientAccessor
{
    /// <summary>Waits until the hosted client is available.</summary>
    /// <param name="cancellationToken">Cancels only this wait.</param>
    /// <returns>The connected hosted client.</returns>
    ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default);
}
