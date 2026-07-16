namespace SharpLink.Abstractions;

/// <summary>Defines the fixed SharpLink Protocol v2 wire constants.</summary>
public static class ProtocolV2Constants
{
    /// <summary>Protocol v2 frame magic.</summary>
    public const byte Magic = 0x89;

    /// <summary>Fixed frame header size in bytes.</summary>
    public const int HeaderBytes = 15;

    /// <summary>Fixed request routing prefix size in bytes.</summary>
    public const int RequestPrefixBytes = 16;

    /// <summary>Current protocol minor version.</summary>
    public const ushort MinorVersion = 1;
}

/// <summary>Protocol v2 frame types.</summary>
public enum ProtocolV2FrameType : byte
{
    HandshakeRequest = 0,
    HandshakeResponse = 1,
    Ping = 2,
    Pong = 3,
    Request = 4,
    Response = 5,
    Cancel = 6,
    StreamData = 7,
    StreamComplete = 8,
    WindowUpdate = 9,
    GoAway = 10,
    HealthCheck = 11,
    HealthResponse = 12
}

/// <summary>Protocol v2 frame flags.</summary>
[Flags]
public enum ProtocolV2FrameFlags : byte
{
    None = 0,
    Error = 1 << 0,
    Truncated = 1 << 1,
    HasDeadline = 1 << 2,
    HasMetadata = 1 << 3,
    Compressed = 1 << 4,
    Cancellable = 1 << 5,
    OneWay = 1 << 6,
    HasReturn = 1 << 7
}

/// <summary>Protocol v2 negotiated capabilities.</summary>
[Flags]
public enum ProtocolV2Capabilities : ulong
{
    None = 0,
    Metadata = 1UL << 0,
    Compression = 1UL << 1,
    FlowControl = 1UL << 2,
    HealthCheck = 1UL << 3
}

/// <summary>Parsed Protocol v2 frame header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct ProtocolV2FrameHeader(
    ProtocolV2FrameType Type,
    ProtocolV2FrameFlags Flags,
    ulong RequestId);

/// <summary>Protocol v2 handshake request values.</summary>
public readonly record struct ProtocolV2HandshakeRequest(
    ushort MinorVersion,
    ProtocolV2Capabilities SupportedCapabilities,
    ProtocolV2Capabilities RequiredCapabilities,
    int MaxFramePayloadBytes,
    int StreamReceiveWindowBytes,
    int ConnectionReceiveWindowBytes,
    ReadOnlyMemory<byte> AuthenticationPayload);

/// <summary>Protocol v2 negotiated handshake response values.</summary>
public readonly record struct ProtocolV2HandshakeResponse(
    ushort MinorVersion,
    ProtocolV2Capabilities NegotiatedCapabilities,
    int MaxFramePayloadBytes,
    int StreamReceiveWindowBytes,
    int ConnectionReceiveWindowBytes);

/// <summary>Returns consumed byte credit for one request stream.</summary>
public readonly record struct ProtocolV2WindowUpdate(
    ushort StreamId,
    uint Credit);

/// <summary>Decoded binary error payload.</summary>
public readonly record struct ProtocolV2Error(
    SharpLinkErrorCode Code,
    string Message,
    bool IsTruncated);
