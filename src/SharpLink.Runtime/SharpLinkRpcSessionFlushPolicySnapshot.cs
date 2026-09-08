namespace SharpLink.Runtime;

/// <summary>Describes the currently published RPC session flush-policy generation.</summary>
public readonly record struct SharpLinkRpcSessionFlushPolicySnapshot(
    ulong Generation,
    int FlushSizeThreshold,
    TimeSpan MaxLatency);
