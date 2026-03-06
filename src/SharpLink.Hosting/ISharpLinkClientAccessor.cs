namespace SharpLink.Hosting;

public interface ISharpLinkClientAccessor
{
    ValueTask<ISharpLinkClient> GetClientAsync(CancellationToken cancellationToken = default);
}
