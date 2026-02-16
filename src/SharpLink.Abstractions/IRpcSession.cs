namespace SharpLink.Abstractions;

public interface IRpcSession : IAsyncDisposable
{
    string Id { get; }
    DateTime LastActive { get; set; }
    PipeReader Input { get; }
    ISerializer Serializer { get; }
    IStreamManager StreamManager { get; }
    bool IsConnected { get; }
    void SendPacket(ArrayBufferWriter<byte> packet);
}
