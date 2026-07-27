namespace SharpLink.Abstractions;

/// <summary>Represents one established RPC transport session.</summary>
public interface IRpcSession : IAsyncDisposable
{
    /// <summary>Gets the session identifier used for diagnostics.</summary>
    string Id { get; }
    /// <summary>Gets the framework runtime context associated with the session.</summary>
    IRpcRuntimeContext RuntimeContext { get; }
    /// <summary>Gets or sets the last UTC time at which valid peer activity was observed.</summary>
    DateTime LastActive { get; set; }
    /// <summary>Gets the transport input consumed by the protocol reader.</summary>
    PipeReader Input { get; }
    /// <summary>Gets the manager for active request and response streams.</summary>
    IStreamManager StreamManager { get; }
    /// <summary>Gets whether the transport session is currently connected.</summary>
    bool IsConnected { get; }
    /// <summary>Occurs after the session becomes connected.</summary>
    event Action OnConnected;
    /// <summary>Notifies subscribers that the session is connected.</summary>
    void NotifyConnected();
    /// <summary>Occurs after the session disconnects, optionally with its terminal error.</summary>
    event Action<Exception?> OnDisconnected;
    /// <summary>Notifies subscribers that the session disconnected.</summary>
    /// <param name="exception">The terminal transport error, or <see langword="null"/> for a normal close.</param>
    void NotifyDisconnected(Exception? exception = null);
}
