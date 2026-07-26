namespace SharpLink.Runtime;

/// <summary>Predefined runtime behavior profiles.</summary>
public enum SharpLinkPerformanceProfile
{
    /// <summary>Balances latency, throughput, and memory usage.</summary>
    Balanced,

    /// <summary>Flushes eagerly and keeps queues small.</summary>
    LowLatency,

    /// <summary>Uses larger bounded queues and batching targets.</summary>
    Throughput
}

/// <summary>Configures bounded buffering and call concurrency for one runtime context.</summary>
public sealed class SharpLinkFlowControlOptions
{
    /// <summary>The hard maximum active Server calls on one physical connection.</summary>
    public const int MaximumConcurrentCallsPerConnection = 1024 * 1024;

    /// <summary>Gets or sets the maximum queued outbound bytes.</summary>
    public int MaxSendQueueBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Gets or sets the initial receive window for one stream.</summary>
    public int StreamReceiveWindowBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the initial receive window for one connection.</summary>
    public int ConnectionReceiveWindowBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum concurrent calls on one connection.</summary>
    public int MaxConcurrentCallsPerConnection { get; set; } = 1024;

    /// <summary>Validates all flow-control limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSendQueueBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StreamReceiveWindowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectionReceiveWindowBytes);
        if (MaxConcurrentCallsPerConnection is < 1 or > MaximumConcurrentCallsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentCallsPerConnection),
                $"MaxConcurrentCallsPerConnection must be between 1 and {MaximumConcurrentCallsPerConnection}.");
        }
        if (ConnectionReceiveWindowBytes < StreamReceiveWindowBytes)
            throw new ArgumentException("ConnectionReceiveWindowBytes cannot be smaller than StreamReceiveWindowBytes.");
    }

    internal SharpLinkFlowControlOptions CloneValidated()
    {
        Validate();
        return new SharpLinkFlowControlOptions
        {
            MaxSendQueueBytes = MaxSendQueueBytes,
            StreamReceiveWindowBytes = StreamReceiveWindowBytes,
            ConnectionReceiveWindowBytes = ConnectionReceiveWindowBytes,
            MaxConcurrentCallsPerConnection = MaxConcurrentCallsPerConnection
        };
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
        if (options.MaxSendQueueBytes != 8 * 1024 * 1024)
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
    {
        destination.MaxSendQueueBytes = source.MaxSendQueueBytes;
        destination.StreamReceiveWindowBytes = source.StreamReceiveWindowBytes;
        destination.ConnectionReceiveWindowBytes = source.ConnectionReceiveWindowBytes;
        destination.MaxConcurrentCallsPerConnection = source.MaxConcurrentCallsPerConnection;
    }

    private static void CopyCompression(
        SharpLinkCompressionOptions source,
        SharpLinkCompressionOptions destination)
    {
        destination.MinimumPayloadBytes = source.MinimumPayloadBytes;
        destination.MinimumSavingsBytes = source.MinimumSavingsBytes;
        destination.MinimumSavingsRatio = source.MinimumSavingsRatio;
        foreach (var provider in source.Providers)
            destination.Providers.Add(provider);
    }
}
