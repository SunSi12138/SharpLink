namespace SharpLink.Abstractions;

public interface IRpcSession : IAsyncDisposable
{
    string Id { get; }
    IRpcRuntimeContext RuntimeContext { get; }
    DateTime LastActive { get; set; }
    PipeReader Input { get; }
    IStreamManager StreamManager { get; }
    bool IsConnected { get; }
    event Action OnConnected;
    void NotifyConnected();
    event Action<Exception?> OnDisconnected;
    void NotifyDisconnected(Exception? exception=null);
}
