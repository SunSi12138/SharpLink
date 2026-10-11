namespace SharpLink.Abstractions;

/// <summary>Describes runtime ABI checks carried directly by one generated UnsafeBlit factory.</summary>
public readonly record struct SharpLinkGeneratedUnsafeBlitRequirement(
    int NativePointerWidth,
    bool RequiresDateTimeOffsetRawAbi);
