namespace SharpLink.Abstractions;

public interface ISharpLinkServer
{
    Task Start(CancellationToken ct = default);
}
