namespace SharpLink.Hosting;

/// <summary>Provides the running client managed by the generic-host lifecycle service.</summary>
public interface ISharpLinkClientAccessor
{
    /// <summary>Waits until the hosted client's local runtime has started.</summary>
    /// <param name="cancellationToken">Cancels only this wait.</param>
    /// <returns>The running hosted client; remote readiness may still be unavailable.</returns>
    ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default);
}
