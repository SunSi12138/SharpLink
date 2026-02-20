namespace SharpLink.Abstractions;

public interface IRpcSession : IAsyncDisposable
{
    string Id { get; }
    DateTime LastActive { get; set; }
    PipeReader Input { get; }
    IStreamManager StreamManager { get; }
    bool IsConnected { get; }
    void SendPacket(ArrayBufferWriter<byte> packet);

    event Action OnConnected;
    void NotifyConnected();
    event Action<Exception?> OnDisconnected;
    void NotifyDisconnected(Exception? exception=null);
}
