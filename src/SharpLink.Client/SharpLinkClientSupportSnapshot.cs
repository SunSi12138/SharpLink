using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.Client;

/// <summary>Bounds one on-demand, redacted client support snapshot.</summary>
public sealed class SharpLinkClientSupportSnapshotOptions
{
    /// <summary>The default maximum number of endpoint entries copied into one snapshot.</summary>
    public const int DefaultMaxEndpoints = 32;

    /// <summary>The default maximum number of connection entries copied into one snapshot.</summary>
    public const int DefaultMaxConnections = 64;

    /// <summary>The default maximum UTF-8 JSON export size: 256 KiB.</summary>
    public const int DefaultMaxJsonBytes = 256 * 1024;

    /// <summary>Gets or sets the maximum number of endpoint entries copied into the snapshot.</summary>
    public int MaxEndpoints { get; set; } = DefaultMaxEndpoints;

    /// <summary>Gets or sets the maximum number of connection entries copied into the snapshot.</summary>
    public int MaxConnections { get; set; } = DefaultMaxConnections;

    /// <summary>Gets or sets the hard UTF-8 byte limit for JSON export.</summary>
    public int MaxJsonBytes { get; set; } = DefaultMaxJsonBytes;

    /// <summary>Gets or sets whether JSON export is indented for attachment to support reports.</summary>
    public bool WriteIndented { get; set; } = true;

    internal SharpLinkClientSupportSnapshotOptions CloneValidated()
    {
        if (MaxEndpoints is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxEndpoints));
        if (MaxConnections is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        if (MaxJsonBytes is < 4096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxJsonBytes));
        return new SharpLinkClientSupportSnapshotOptions
        {
            MaxEndpoints = MaxEndpoints,
            MaxConnections = MaxConnections,
            MaxJsonBytes = MaxJsonBytes,
            WriteIndented = WriteIndented
        };
    }
}

/// <summary>Identifies the topology shape without exposing endpoint identities.</summary>
public enum SharpLinkSupportTopologyKind : byte
{
    Fixed,
    Static,
    Dynamic
}

/// <summary>Identifies a transport category without exposing its address.</summary>
public enum SharpLinkSupportTransportKind : byte
{
    Custom,
    Tcp,
    UnixDomainSocket,
    NamedPipe,
    AnonymousPipe,
    SharedMemory
}

/// <summary>Describes an endpoint's support-facing lifecycle state.</summary>
public enum SharpLinkSupportEndpointState : byte
{
    Unavailable,
    Ready,
    Retiring
}

/// <summary>Describes a physical client connection without exposing transport endpoints.</summary>
public enum SharpLinkSupportConnectionState : byte
{
    Ready,
    Draining,
    Closed
}

/// <summary>Identifies the stage of the most recently observed connection failure.</summary>
public enum SharpLinkConnectionFailureStage : byte
{
    Resolve,
    Dial,
    Tls,
    Handshake,
    Authentication,
    Protocol,
    Readiness,
    Unknown
}

/// <summary>Provides a coarse, non-secret failure classification suitable for public issue attachments.</summary>
public enum SharpLinkConnectionFailureClass : byte
{
    Timeout,
    Cancelled,
    Refused,
    Authentication,
    Protocol,
    Version,
    Resource,
    Transport,
    Internal
}

/// <summary>Root schema for one point-in-time, redacted client support artifact.</summary>
public sealed record SharpLinkClientSupportSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    SharpLinkSupportRuntimeSnapshot Runtime,
    SharpLinkSupportConfigurationSnapshot Configuration,
    SharpLinkClientReadinessSnapshot Readiness,
    SharpLinkSupportTopologySnapshot Topology,
    SharpLinkSupportResourceSnapshot Resources,
    SharpLinkConnectionFailureSnapshot? LastConnectionFailure);

/// <summary>Safe runtime/package identity fields.</summary>
public sealed record SharpLinkSupportRuntimeSnapshot(
    string SharpLinkVersion,
    string RuntimeDescription,
    string OperatingSystem,
    string ProcessArchitecture,
    string Protocol,
    SharpLinkPerformanceProfile PerformanceProfile);

/// <summary>Effective non-secret client limits and policy generations.</summary>
public sealed record SharpLinkSupportConfigurationSnapshot(
    SharpLinkRequestTimeoutPolicySnapshot RequestTimeout,
    SharpLinkHeartbeatConfigurationSnapshot Heartbeat,
    SharpLinkReconnectPolicy Reconnect,
    SharpLinkRetryPolicySnapshot Retry,
    SharpLinkEndpointAdmissionPolicySnapshot EndpointAdmission,
    SharpLinkCircuitBreakerPolicySnapshot CircuitBreaker,
    SharpLinkEndpointSelectionPolicySnapshot? EndpointSelection,
    TimeSpan HandshakeTimeout,
    int MaxPendingRequestsPerConnection,
    int MaxConcurrentStreamsPerConnection,
    int MaxSendQueueBytes,
    int MinConnectionsPerEndpoint,
    int MaxConnectionsPerEndpoint,
    bool AuthenticationConfigured,
    SharpLinkSupportCompressionPolicySnapshot RequestCompression,
    bool ResponseCompressionAllowed);

/// <summary>Non-secret request-compression decision thresholds.</summary>
public sealed record SharpLinkSupportCompressionPolicySnapshot(
    bool Enabled,
    int MinimumPayloadBytes,
    int MinimumSavingsBytes,
    double MinimumSavingsRatio);

/// <summary>Bounded endpoint and connection inventory.</summary>
public sealed record SharpLinkSupportTopologySnapshot(
    SharpLinkSupportTopologyKind Kind,
    int TotalEndpoints,
    int CapturedEndpoints,
    bool EndpointsTruncated,
    int TotalConnections,
    int CapturedConnections,
    bool ConnectionsTruncated,
    IReadOnlyList<SharpLinkSupportEndpointSnapshot> Endpoints,
    IReadOnlyList<SharpLinkSupportConnectionSnapshot> Connections);

/// <summary>One redacted endpoint entry. <see cref="SafeId"/> is only an ordinal inside this snapshot.</summary>
public sealed record SharpLinkSupportEndpointSnapshot(
    string SafeId,
    SharpLinkSupportTransportKind Transport,
    bool AuthorityConfigured,
    SharpLinkSupportEndpointState State,
    long? Generation,
    int ReadyConnections,
    int ActiveConnections,
    int RetiringConnections,
    int ConnectingConnections);

/// <summary>One redacted physical connection entry.</summary>
public sealed record SharpLinkSupportConnectionSnapshot(
    string SafeId,
    string EndpointSafeId,
    SharpLinkSupportConnectionState State,
    bool CanAcceptCalls,
    int ActiveCalls,
    SharpLinkSupportConnectionResourceSnapshot Resources,
    SharpLinkSupportNegotiationSnapshot Negotiation);

/// <summary>Existing owner counters captured for one physical connection.</summary>
public sealed record SharpLinkSupportConnectionResourceSnapshot(
    int PendingRequests,
    int PendingRequestCapacity,
    int PendingRequestWaiters,
    int SendQueuedBytes,
    int SendQueueLimitBytes,
    int ActiveStreams,
    int StreamLimit);

/// <summary>Safe protocol/security details captured from an established session.</summary>
public sealed record SharpLinkSupportNegotiationSnapshot(
    string ProtocolPhase,
    int ProtocolMajor,
    ushort? ProtocolMinor,
    string? Capabilities,
    bool CompressionNegotiated,
    int? MaxFramePayloadBytes,
    int? StreamReceiveWindowBytes,
    int? ConnectionReceiveWindowBytes,
    bool Tls,
    string? TlsProtocol,
    string? CipherSuite);

/// <summary>Aggregate resource counts derived from existing connection owners at capture time.</summary>
public sealed record SharpLinkSupportResourceSnapshot(
    int PendingRequests,
    int ActiveCalls,
    int ActiveStreams,
    long SendQueuedBytes,
    int ReadyConnections);

/// <summary>One already-redacted last-known connection failure publication.</summary>
public sealed record SharpLinkConnectionFailureSnapshot(
    SharpLinkConnectionFailureStage Stage,
    SharpLinkConnectionFailureClass Classification,
    string? ErrorCode,
    string ExceptionType,
    string? EndpointSafeId,
    DateTimeOffset OccurredAtUtc,
    TimeSpan Age);

/// <summary>Creates bounded support snapshots and JSON attachments from the built-in SharpLink client.</summary>
public static class SharpLinkClientDiagnosticsExtensions
{
    /// <summary>Captures a redacted point-in-time diagnostic snapshot without retaining request data.</summary>
    public static SharpLinkClientSupportSnapshot GetDiagnosticSnapshot(
        this ISharpLinkClient client,
        SharpLinkClientSupportSnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client is not SharpLinkClient runtime)
            throw new NotSupportedException("This ISharpLinkClient implementation does not expose SharpLink support snapshots.");
        return runtime.CaptureSupportSnapshot((options ?? new SharpLinkClientSupportSnapshotOptions()).CloneValidated());
    }

    /// <summary>Captures first, then serializes a redacted snapshot with a hard UTF-8 size limit.</summary>
    public static string ExportDiagnosticSnapshotJson(
        this ISharpLinkClient client,
        SharpLinkClientSupportSnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var validated = (options ?? new SharpLinkClientSupportSnapshotOptions()).CloneValidated();
        var snapshot = client.GetDiagnosticSnapshot(validated);
        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = validated.WriteIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(snapshot, serializerOptions);
        if (utf8.Length > validated.MaxJsonBytes)
        {
            throw new InvalidOperationException(
                $"The redacted diagnostic snapshot is {utf8.Length} UTF-8 bytes, exceeding the configured {validated.MaxJsonBytes}-byte export limit.");
        }
        return Encoding.UTF8.GetString(utf8);
    }
}
