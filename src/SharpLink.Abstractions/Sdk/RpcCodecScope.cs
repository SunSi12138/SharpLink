namespace SharpLink.Sdk;

/// <summary>Classifies configurable RPC payload types for compile-time Codec routing.</summary>
[Flags]
public enum RpcCodecScope
{
    /// <summary>No payload types.</summary>
    None = 0,

    /// <summary>Configurable payloads that contain managed references.</summary>
    Managed = 1 << 0,

    /// <summary>Configurable unmanaged payloads.</summary>
    Unmanaged = 1 << 1,

    /// <summary>All configurable RPC payload scopes.</summary>
    All = Managed | Unmanaged
}
