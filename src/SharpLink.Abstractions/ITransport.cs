namespace SharpLink.Abstractions;
/// <summary>
/// 传输层抽象
/// </summary>
public interface ITransport : IDisposable
{
    Task<IRpcSession> ConnectAsync(ISerializer serializer,CancellationToken ct=default);
}
