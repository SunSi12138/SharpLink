namespace SharpLink.Abstractions;

/// <summary>Describes the lifecycle state of one SharpLink client.</summary>
public enum SharpLinkConnectionState
{
    /// <summary>The client has been built but has not started connecting.</summary>
    Created,

    /// <summary>A client-owned initial connection attempt is running.</summary>
    Connecting,

    /// <summary>At least one connection is ready to accept new calls.</summary>
    Ready,

    /// <summary>The current connection is no longer accepting new calls.</summary>
    Draining,

    /// <summary>The client is attempting to create a replacement connection.</summary>
    Reconnecting,

    /// <summary>The client has stopped and cannot be restarted.</summary>
    Stopped,

    /// <summary>The last explicit initial connection attempt failed.</summary>
    Faulted
}
