namespace SharpLink.Abstractions;

public interface ISharpLinkClient
{
    Task<bool> ConnectAsync(CancellationToken ct = default);
    public T Get<T>() where T : IService;
}
