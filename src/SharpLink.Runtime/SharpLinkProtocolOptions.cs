namespace SharpLink.Runtime;

/// <summary>
/// Configures protocol safety limits applied independently by each SharpLink client or server.
/// </summary>
/// <example>
/// <code>
/// var client = SharpClientBuilder.Create()
///     .UseProtocol(options =&gt; options.MaxFramePayloadBytes = 8 * 1024 * 1024);
/// </code>
/// </example>
public sealed class SharpLinkProtocolOptions
{
    /// <summary>The default maximum frame payload size: 4 MiB.</summary>
    public const int DefaultMaxFramePayloadBytes = 4 * 1024 * 1024;

    /// <summary>The minimum supported maximum frame payload size: 1 KiB.</summary>
    public const int MinMaxFramePayloadBytes = 1024;

    /// <summary>The largest configurable maximum frame payload size: 64 MiB.</summary>
    public const int MaxMaxFramePayloadBytes = 64 * 1024 * 1024;

    /// <summary>The default maximum metadata or handshake authentication payload size: 16 KiB.</summary>
    public const int DefaultMaxMetadataBytes = 16 * 1024;

    /// <summary>The default maximum remote error message size: 64 KiB.</summary>
    public const int DefaultMaxErrorMessageBytes = 64 * 1024;

    /// <summary>The hard maximum pending-request table capacity per physical Client connection.</summary>
    public const int MaximumPendingRequestsPerConnection = 1024 * 1024;

    /// <summary>Gets or sets the largest payload accepted for a single protocol frame.</summary>
    public int MaxFramePayloadBytes { get; set; } = DefaultMaxFramePayloadBytes;

    /// <summary>Gets or sets the maximum metadata size reserved for protocol v2.</summary>
    public int MaxMetadataBytes { get; set; } = DefaultMaxMetadataBytes;

    /// <summary>Gets or sets the maximum remote error message size reserved for protocol v2.</summary>
    public int MaxErrorMessageBytes { get; set; } = DefaultMaxErrorMessageBytes;

    /// <summary>Gets or sets the maximum time allowed for the RPC handshake.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum pending requests on one connection.</summary>
    public int MaxPendingRequestsPerConnection { get; set; } = 65_536;

    /// <summary>Gets or sets the maximum active streams on one connection.</summary>
    public int MaxConcurrentStreamsPerConnection { get; set; } = 1_024;

    /// <summary>Validates all configured protocol limits.</summary>
    public void Validate()
    {
        if (MaxFramePayloadBytes is < MinMaxFramePayloadBytes or > MaxMaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFramePayloadBytes),
                $"MaxFramePayloadBytes must be between {MinMaxFramePayloadBytes} and {MaxMaxFramePayloadBytes} bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxErrorMessageBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandshakeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingRequestsPerConnection);
        if (MaxPendingRequestsPerConnection > MaximumPendingRequestsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPendingRequestsPerConnection),
                $"MaxPendingRequestsPerConnection cannot exceed {MaximumPendingRequestsPerConnection}.");
        }
        if (!BitOperations.IsPow2(MaxPendingRequestsPerConnection))
            throw new ArgumentException("MaxPendingRequestsPerConnection must be a power of two.", nameof(MaxPendingRequestsPerConnection));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentStreamsPerConnection);
    }

    /// <summary>Creates a validated copy that is isolated from later builder mutations.</summary>
    public SharpLinkProtocolOptions CloneValidated()
    {
        Validate();
        return new SharpLinkProtocolOptions
        {
            MaxFramePayloadBytes = MaxFramePayloadBytes,
            MaxMetadataBytes = MaxMetadataBytes,
            MaxErrorMessageBytes = MaxErrorMessageBytes,
            HandshakeTimeout = HandshakeTimeout,
            MaxPendingRequestsPerConnection = MaxPendingRequestsPerConnection,
            MaxConcurrentStreamsPerConnection = MaxConcurrentStreamsPerConnection
        };
    }
}
