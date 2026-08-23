namespace SharpLink.Runtime;

/// <summary>Predefined runtime behavior profiles.</summary>
public enum SharpLinkPerformanceProfile
{
    /// <summary>Balances latency, throughput, and memory usage.</summary>
    Balanced,

    /// <summary>Flushes eagerly and keeps queues small.</summary>
    LowLatency,

    /// <summary>Uses larger bounded queues and a larger batching threshold. The batch is
    /// flushed as soon as the outbound queue drains, so frames of an active pipeline leave
    /// immediately; callers that want deadline-bounded batching configure an explicit
    /// <c>RpcSessionFlushOptions.MaxLatency</c> instead.</summary>
    Throughput
}

/// <summary>Configures bounded buffering and call concurrency for one runtime context.</summary>
public sealed class SharpLinkFlowControlOptions
{
    private const int DefaultMaxSendQueueBytes = 8 * 1024 * 1024;
    private const int DefaultMaxPreCreditSerializedBytes = 4 * 1024 * 1024;
    private int _maxSendQueueBytes = DefaultMaxSendQueueBytes;
    private bool _maxSendQueueBytesConfigured;

    /// <summary>The hard maximum active Server calls on one physical connection.</summary>
    public const int MaximumConcurrentCallsPerConnection = 1024 * 1024;

    /// <summary>The default maximum active calls across one server instance.</summary>
    public const int DefaultMaxConcurrentCallsPerServer = 65_536;

    /// <summary>The hard maximum active calls across one server instance.</summary>
    public const int MaximumConcurrentCallsPerServer = 1024 * 1024;

    /// <summary>The default maximum concurrent compression decodes across one server instance.</summary>
    public const int DefaultMaxConcurrentDecodesPerServer = 32;

    /// <summary>The default server-wide retained compressed-byte budget: 64 MiB.</summary>
    public const long DefaultMaxRetainedCompressedBytesPerServer = 64L * 1024 * 1024;

    /// <summary>The default server-wide decoded-byte in-flight budget: 64 MiB.</summary>
    public const long DefaultMaxDecodedBytesInFlightPerServer = 64L * 1024 * 1024;

    /// <summary>Gets or sets the maximum queued outbound bytes.</summary>
    public int MaxSendQueueBytes
    {
        get => _maxSendQueueBytes;
        set
        {
            _maxSendQueueBytes = value;
            _maxSendQueueBytesConfigured = true;
        }
    }

    /// <summary>
    /// Gets or sets the local owner/admission byte budget for fully serialized unsized streaming
    /// items that have been admitted while waiting for flow-control credit. The default is 4 MiB.
    /// </summary>
    /// <remarks>
    /// This is a local process-memory admission limit. It is independent from
    /// <see cref="ConnectionReceiveWindowBytes"/>, is not sent during protocol negotiation, and
    /// does not change peer-visible flow-control credit. This value bounds admitted byte owners;
    /// it is not an aggregate cap over serialized writers already queued as bounded budget waiters.
    /// A legal item larger than this value may temporarily borrow the budget as the sole owner so
    /// a small budget does not make a legal frame permanently unsendable.
    ///
    /// If B is this budget, F is the negotiated maximum frame payload, and S is the concurrent
    /// stream limit, the waiter cap is W = min(S, max(1, floor(B / F))). The long-lived serialized
    /// payload held by this subsystem is therefore bounded by max(B, F) + W * F before frame/header
    /// overhead and buffer-pool capacity rounding. With the default B = F = 4 MiB, W = 1 and the
    /// aggregate serialized-payload envelope is at most 8 MiB before that overhead.
    /// </remarks>
    public int MaxPreCreditSerializedBytes { get; set; } = DefaultMaxPreCreditSerializedBytes;

    /// <summary>Gets or sets the initial receive window for one stream.</summary>
    public int StreamReceiveWindowBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the initial receive window for one connection.</summary>
    public int ConnectionReceiveWindowBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum concurrent calls on one connection.</summary>
    public int MaxConcurrentCallsPerConnection { get; set; } = 1024;

    /// <summary>Gets or sets the maximum concurrent calls across one server instance.</summary>
    /// <remarks>
    /// This is independent from <see cref="MaxConcurrentCallsPerConnection"/>. A call must fit
    /// under both limits before it can execute.
    /// </remarks>
    public int MaxConcurrentCallsPerServer { get; set; } = DefaultMaxConcurrentCallsPerServer;

    /// <summary>
    /// Gets or sets the hard maximum number of provider decompressions that may execute concurrently
    /// across one server instance.
    /// </summary>
    public int MaxConcurrentDecodesPerServer { get; set; } = DefaultMaxConcurrentDecodesPerServer;

    /// <summary>
    /// Gets or sets the server-wide byte budget for compressed request payloads retained beyond the
    /// reader-loop frame lifetime while waiting for or executing deferred decode.
    /// </summary>
    public long MaxRetainedCompressedBytesPerServer { get; set; } = DefaultMaxRetainedCompressedBytesPerServer;

    /// <summary>
    /// Gets or sets the server-wide byte budget for decoded request payload storage that remains
    /// owned by admitted requests.
    /// </summary>
    public long MaxDecodedBytesInFlightPerServer { get; set; } = DefaultMaxDecodedBytesInFlightPerServer;

    /// <summary>Validates all flow-control limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSendQueueBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPreCreditSerializedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StreamReceiveWindowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectionReceiveWindowBytes);
        if (MaxConcurrentCallsPerConnection is < 1 or > MaximumConcurrentCallsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentCallsPerConnection),
                $"MaxConcurrentCallsPerConnection must be between 1 and {MaximumConcurrentCallsPerConnection}.");
        }
        if (MaxConcurrentCallsPerServer is < 1 or > MaximumConcurrentCallsPerServer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentCallsPerServer),
                $"MaxConcurrentCallsPerServer must be between 1 and {MaximumConcurrentCallsPerServer}.");
        }
        if (MaxConcurrentDecodesPerServer is < 1 or > MaximumConcurrentCallsPerServer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentDecodesPerServer),
                $"MaxConcurrentDecodesPerServer must be between 1 and {MaximumConcurrentCallsPerServer}.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedCompressedBytesPerServer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDecodedBytesInFlightPerServer);
        if (ConnectionReceiveWindowBytes < StreamReceiveWindowBytes)
            throw new ArgumentException("ConnectionReceiveWindowBytes cannot be smaller than StreamReceiveWindowBytes.");
    }

    internal SharpLinkFlowControlOptions CloneValidated()
    {
        Validate();
        var clone = new SharpLinkFlowControlOptions
        {
            MaxPreCreditSerializedBytes = MaxPreCreditSerializedBytes,
            StreamReceiveWindowBytes = StreamReceiveWindowBytes,
            ConnectionReceiveWindowBytes = ConnectionReceiveWindowBytes,
            MaxConcurrentCallsPerConnection = MaxConcurrentCallsPerConnection,
            MaxConcurrentCallsPerServer = MaxConcurrentCallsPerServer,
            MaxConcurrentDecodesPerServer = MaxConcurrentDecodesPerServer,
            MaxRetainedCompressedBytesPerServer = MaxRetainedCompressedBytesPerServer,
            MaxDecodedBytesInFlightPerServer = MaxDecodedBytesInFlightPerServer
        };
        clone._maxSendQueueBytes = _maxSendQueueBytes;
        clone._maxSendQueueBytesConfigured = _maxSendQueueBytesConfigured;
        return clone;
    }

    internal bool HasConfiguredMaxSendQueueBytes => _maxSendQueueBytesConfigured;

    internal void CopySnapshotTo(SharpLinkFlowControlOptions destination)
    {
        destination._maxSendQueueBytes = _maxSendQueueBytes;
        destination._maxSendQueueBytesConfigured = _maxSendQueueBytesConfigured;
        destination.MaxPreCreditSerializedBytes = MaxPreCreditSerializedBytes;
        destination.StreamReceiveWindowBytes = StreamReceiveWindowBytes;
        destination.ConnectionReceiveWindowBytes = ConnectionReceiveWindowBytes;
        destination.MaxConcurrentCallsPerConnection = MaxConcurrentCallsPerConnection;
        destination.MaxConcurrentCallsPerServer = MaxConcurrentCallsPerServer;
        destination.MaxConcurrentDecodesPerServer = MaxConcurrentDecodesPerServer;
        destination.MaxRetainedCompressedBytesPerServer = MaxRetainedCompressedBytesPerServer;
        destination.MaxDecodedBytesInFlightPerServer = MaxDecodedBytesInFlightPerServer;
    }
}

/// <summary>Mutable builder options that are frozen when a client or server is built.</summary>
/// <example>
/// <code>
/// builder.UseRuntime(options =&gt;
/// {
///     options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency;
///     options.Protocol.MaxFramePayloadBytes = 8 * 1024 * 1024;
/// });
/// </code>
/// </example>
public sealed class SharpLinkRuntimeOptions
{
    /// <summary>Gets or sets the selected performance profile.</summary>
    public SharpLinkPerformanceProfile PerformanceProfile { get; set; } = SharpLinkPerformanceProfile.Balanced;

    /// <summary>Gets protocol safety limits.</summary>
    public SharpLinkProtocolOptions Protocol { get; } = new();

    /// <summary>Gets flow-control and concurrency limits.</summary>
    public SharpLinkFlowControlOptions FlowControl { get; } = new();

    /// <summary>Gets negotiated payload-compression options. An empty provider list disables compression.</summary>
    public SharpLinkCompressionOptions Compression { get; } = new();

    internal SharpLinkRuntimeOptions CloneValidated()
    {
        if (!Enum.IsDefined(PerformanceProfile))
            throw new ArgumentOutOfRangeException(nameof(PerformanceProfile));

        var clone = new SharpLinkRuntimeOptions { PerformanceProfile = PerformanceProfile };
        CopyProtocol(Protocol.CloneValidated(), clone.Protocol);
        CopyFlowControl(ApplyProfileDefaults(FlowControl.CloneValidated(), PerformanceProfile), clone.FlowControl);
        CopyCompression(Compression.CloneValidated(), clone.Compression);
        return clone;
    }

    private static SharpLinkFlowControlOptions ApplyProfileDefaults(
        SharpLinkFlowControlOptions options,
        SharpLinkPerformanceProfile profile)
    {
        if (options.HasConfiguredMaxSendQueueBytes)
            return options;

        options.MaxSendQueueBytes = profile switch
        {
            SharpLinkPerformanceProfile.LowLatency => 1024 * 1024,
            SharpLinkPerformanceProfile.Throughput => 32 * 1024 * 1024,
            _ => 8 * 1024 * 1024
        };
        return options;
    }

    private static void CopyProtocol(SharpLinkProtocolOptions source, SharpLinkProtocolOptions destination)
    {
        destination.MaxFramePayloadBytes = source.MaxFramePayloadBytes;
        destination.MaxMetadataBytes = source.MaxMetadataBytes;
        destination.MaxErrorMessageBytes = source.MaxErrorMessageBytes;
        destination.HandshakeTimeout = source.HandshakeTimeout;
        destination.MaxPendingRequestsPerConnection = source.MaxPendingRequestsPerConnection;
        destination.MaxConcurrentStreamsPerConnection = source.MaxConcurrentStreamsPerConnection;
    }

    private static void CopyFlowControl(SharpLinkFlowControlOptions source, SharpLinkFlowControlOptions destination)
        => source.CopySnapshotTo(destination);

    private static void CopyCompression(
        SharpLinkCompressionOptions source,
        SharpLinkCompressionOptions destination)
        => source.CopyValidatedSnapshotTo(destination);
}
