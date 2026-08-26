namespace SharpLink.Sdk;

/// <summary>Classifies RPC payload types for compile-time Codec routing.</summary>
[Flags]
public enum RpcCodecScope
{
    /// <summary>No payload types.</summary>
    None = 0,

    /// <summary>Non-native payloads that contain managed references.</summary>
    Managed = 1 << 0,

    /// <summary>Non-native unmanaged payloads that would otherwise use the unmanaged fallback.</summary>
    Unmanaged = 1 << 1,

    /// <summary>Payloads with a deterministic SharpLink native Codec path.</summary>
    Native = 1 << 2,

    /// <summary>All RPC payload scopes.</summary>
    All = Managed | Unmanaged | Native
}
