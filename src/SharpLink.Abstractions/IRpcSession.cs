namespace SharpLink.Abstractions;

public interface IRpcSession : IDisposable
{
    string Id { get; }
    DateTime LastActive { get; set; }
    PipeReader Input { get; }
    ISerializer Serializer { get; }
    IStreamManager StreamManager { get; }
    bool IsConnected { get; }
    bool SendPacket(ArrayBufferWriter<byte> packet);
}
