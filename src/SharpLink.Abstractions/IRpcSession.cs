namespace SharpLink.Abstractions;

public interface IRpcSession : IAsyncDisposable
{
    string Id { get; }
    DateTime LastActive { get; set; }
    PipeReader Input { get; }
    IStreamManager StreamManager { get; }
    bool IsConnected { get; }
    void SendPacket(ArrayBufferWriter<byte> packet);
}
