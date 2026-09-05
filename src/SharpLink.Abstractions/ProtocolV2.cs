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
    public const ushort MinorVersion = 6;

    /// <summary>Protocol minors below this floor predate the current wire generation and are not wire-compatible.</summary>
    public const ushort MinimumCompatibleMinorVersion = 6;
}

/// <summary>Protocol v2 frame types.</summary>
public enum ProtocolV2FrameType : byte
{
    /// <summary>Begins capability negotiation and client authentication.</summary>
    HandshakeRequest = 0,
    /// <summary>Completes capability negotiation or reports a handshake failure.</summary>
    HandshakeResponse = 1,
    /// <summary>Requests a liveness response from the peer.</summary>
    Ping = 2,
    /// <summary>Responds to a <see cref="Ping"/> frame.</summary>
    Pong = 3,
    /// <summary>Starts an RPC request.</summary>
    Request = 4,
    /// <summary>Completes an RPC request with a value or error.</summary>
    Response = 5,
    /// <summary>Cancels or abandons an active request.</summary>
    Cancel = 6,
    /// <summary>Carries one chunk of request or response stream data.</summary>
    StreamData = 7,
    /// <summary>Marks one direction of a stream as complete.</summary>
    StreamComplete = 8,
    /// <summary>Returns flow-control credit to a stream or connection.</summary>
    WindowUpdate = 9,
    /// <summary>Instructs the peer to stop creating calls and drain the connection.</summary>
    GoAway = 10,
    /// <summary>Requests the endpoint's health status.</summary>
    HealthCheck = 11,
    /// <summary>Returns the endpoint's health status.</summary>
    HealthResponse = 12,
    /// <summary>Publishes the server's current contract-to-assembly wire identities.</summary>
    ContractManifest = 13
}

/// <summary>Protocol v2 frame flags.</summary>
[Flags]
public enum ProtocolV2FrameFlags : byte
{
    /// <summary>No optional frame semantics are enabled.</summary>
    None = 0,
    /// <summary>The payload describes an error instead of a successful result.</summary>
    Error = 1 << 0,
    /// <summary>The payload was truncated to remain within a configured limit.</summary>
    Truncated = 1 << 1,
    /// <summary>The request prefix contains a remaining RPC time budget.</summary>
    HasTimeBudget = 1 << 2,
    /// <summary>The request contains metadata.</summary>
    HasMetadata = 1 << 3,
    /// <summary>The payload uses the negotiated compression profile.</summary>
    Compressed = 1 << 4,
    /// <summary>The request may be canceled by its caller.</summary>
    Cancellable = 1 << 5,
    /// <summary>The request does not expect a response frame.</summary>
    OneWay = 1 << 6,
    /// <summary>The response carries a return value.</summary>
    HasReturn = 1 << 7
}

/// <summary>Protocol v2 negotiated capabilities.</summary>
[Flags]
public enum ProtocolV2Capabilities : ulong
{
    /// <summary>No optional protocol capability is supported.</summary>
    None = 0,
    /// <summary>Supports request metadata.</summary>
    Metadata = 1UL << 0,
    /// <summary>Supports negotiated payload compression.</summary>
    Compression = 1UL << 1,
    /// <summary>Supports stream and connection receive-window flow control.</summary>
    FlowControl = 1UL << 2,
    /// <summary>Supports protocol-level health checks.</summary>
    HealthCheck = 1UL << 3,
    /// <summary>Negotiates an explicit one-byte reason on Cancel frames.</summary>
    CancellationReason = 1UL << 4,
    /// <summary>Publishes deterministic contract-assembly identities for bind-time compatibility checks.</summary>
    ContractManifest = 1UL << 5
}

/// <summary>Identifies why a client abandoned an active RPC call.</summary>
public enum ProtocolV2CancelReason : byte
{
    /// <summary>The peer uses the legacy Cancel form or did not provide a more specific reason.</summary>
    Unspecified = 0,
    /// <summary>The caller's cancellation token was canceled.</summary>
    UserCancellation = 1,
    /// <summary>The effective RPC deadline expired.</summary>
    DeadlineExceeded = 2,
    /// <summary>The caller stopped consuming a streaming response before remote completion.</summary>
    ConsumerAbandoned = 3
}

/// <summary>Parsed Protocol v2 frame header.</summary>
/// <param name="Type">The operation represented by the frame.</param>
/// <param name="Flags">Optional semantics applied to the frame.</param>
/// <param name="RequestId">The connection-scoped request identifier, or zero for connection frames.</param>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct ProtocolV2FrameHeader(
    ProtocolV2FrameType Type,
    ProtocolV2FrameFlags Flags,
    ulong RequestId);

/// <summary>Protocol v2 handshake request values.</summary>
/// <param name="MinorVersion">The highest protocol minor version supported by the client.</param>
/// <param name="SupportedCapabilities">Optional capabilities supported by the client.</param>
/// <param name="RequiredCapabilities">Capabilities that the server must negotiate for the connection to succeed.</param>
/// <param name="MaxFramePayloadBytes">The largest frame payload accepted by the client.</param>
/// <param name="StreamReceiveWindowBytes">The client's initial receive window for each stream.</param>
/// <param name="ConnectionReceiveWindowBytes">The client's initial aggregate receive window for the connection.</param>
/// <param name="AuthenticationPayload">Opaque credentials supplied to the server authentication provider.</param>
/// <param name="CompressionProfiles">Compression profiles supported by the client, in preference order.</param>
public readonly record struct ProtocolV2HandshakeRequest(
    ushort MinorVersion,
    ProtocolV2Capabilities SupportedCapabilities,
    ProtocolV2Capabilities RequiredCapabilities,
    int MaxFramePayloadBytes,
    int StreamReceiveWindowBytes,
    int ConnectionReceiveWindowBytes,
    ReadOnlyMemory<byte> AuthenticationPayload,
    ReadOnlyMemory<string> CompressionProfiles = default);

/// <summary>Protocol v2 negotiated handshake response values.</summary>
/// <param name="MinorVersion">The protocol minor version selected by the server.</param>
/// <param name="NegotiatedCapabilities">Optional capabilities enabled for the connection.</param>
/// <param name="MaxFramePayloadBytes">The largest frame payload accepted on the connection.</param>
/// <param name="StreamReceiveWindowBytes">The negotiated initial receive window for each stream.</param>
/// <param name="ConnectionReceiveWindowBytes">The negotiated aggregate receive window for the connection.</param>
/// <param name="CompressionProfile">The selected compression profile, or <see langword="null"/> when compression is disabled.</param>
public readonly record struct ProtocolV2HandshakeResponse(
    ushort MinorVersion,
    ProtocolV2Capabilities NegotiatedCapabilities,
    int MaxFramePayloadBytes,
    int StreamReceiveWindowBytes,
    int ConnectionReceiveWindowBytes,
    string? CompressionProfile = null);

/// <summary>Returns consumed byte credit for one request stream.</summary>
/// <param name="StreamId">The request-local stream identifier, or zero for connection-level credit.</param>
/// <param name="Credit">The number of additional payload bytes the sender may transmit.</param>
public readonly record struct ProtocolV2WindowUpdate(
    ushort StreamId,
    uint Credit);

/// <summary>Decoded binary error payload.</summary>
/// <param name="Code">The coarse machine-readable error classification.</param>
/// <param name="DetailCode">The stable detail code scoped by <paramref name="Code"/>.</param>
/// <param name="Message">The diagnostic message returned by the endpoint.</param>
/// <param name="IsTruncated">Whether the endpoint truncated <paramref name="Message"/>.</param>
public readonly record struct ProtocolV2Error(
    SharpLinkErrorCode Code,
    ushort DetailCode,
    string Message,
    bool IsTruncated);
